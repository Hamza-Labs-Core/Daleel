using System.Collections.Concurrent;
using System.Net;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// Answers "can a user actually open this link?" for result offers. Deliberately conservative
/// about filtering: only failures that hit REAL USERS too — DNS that doesn't resolve, refused/
/// timed-out connections, and gone/legally-blocked statuses — count as unreachable. Bot-defenses
/// (401/403/429) count as reachable: the store blocks OUR probe, not the shopper's browser.
/// </summary>
public interface IReachabilityProbe
{
    Task<bool> IsReachableAsync(string url, CancellationToken ct = default);
}

public sealed class ReachabilityProbe : IReachabilityProbe
{
    /// <summary>Per-host verdict cache — one probe answers for every offer on that host.</summary>
    private readonly ConcurrentDictionary<string, (bool Reachable, DateTimeOffset At)> _hosts = new();

    private static readonly TimeSpan VerdictTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    private readonly HttpClient _http;

    public ReachabilityProbe(HttpClient http)
    {
        _http = http;
        // A browser-plausible UA — a bare HttpClient UA trips bot-defenses that would misclassify
        // perfectly reachable stores.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        _http.Timeout = ProbeTimeout;
    }

    public async Task<bool> IsReachableAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (_hosts.TryGetValue(uri.Host, out var cached) && DateTimeOffset.UtcNow - cached.At < VerdictTtl)
        {
            return cached.Reachable;
        }

        var reachable = await ProbeAsync(uri, ct).ConfigureAwait(false);
        _hosts[uri.Host] = (reachable, DateTimeOffset.UtcNow);
        return reachable;
    }

    private async Task<bool> ProbeAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, uri);
                using var getResponse = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                return Classify(getResponse.StatusCode);
            }

            return Classify(response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the unit's budget, not the site's fault
        }
        catch
        {
            // DNS failure, refused connection, TLS error, probe timeout — a user's browser hits the
            // same wall. This is exactly the ashrafi-mills.com case (NXDOMAIN).
            return false;
        }
    }

    private static bool Classify(HttpStatusCode status) => (int)status switch
    {
        // Bot-defense statuses are REACHABLE: the shopper's real browser typically gets through.
        401 or 403 or 429 => true,
        // Gone, never-existed, or legally blocked — the user hits the same dead end.
        404 or 410 or 451 => false,
        >= 500 => false,
        _ => true
    };
}
