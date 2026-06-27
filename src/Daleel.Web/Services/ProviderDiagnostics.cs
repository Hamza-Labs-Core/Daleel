using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace Daleel.Web.Services;

/// <summary>Outcome of a raw provider probe — the HTTP status, latency, and the raw response body.</summary>
public sealed record DiagResult(int Status, long LatencyMs, string Body, string? Error)
{
    public bool Ok => Error is null && Status is >= 200 and < 300;
}

/// <summary>
/// A QA-only diagnostic that fires raw HTTP calls at the external providers (using the server's
/// configured keys) and returns the raw response — so we can discover the exact endpoints/shapes a
/// provider like Context.dev actually accepts, without guessing from docs. Gated behind
/// <c>DIAGNOSTICS_ENABLED</c>: it is a hard no-op unless that flag is set (QA sets it, production
/// does not), so raw provider access is never exposed in production.
/// </summary>
public interface IProviderDiagnostics
{
    /// <summary>True only when DIAGNOSTICS_ENABLED is set (QA). Everything no-ops otherwise.</summary>
    bool Enabled { get; }

    /// <summary>Raw call to api.context.dev — pick the method, path (+query), and optional JSON body.</summary>
    Task<DiagResult> ContextDevRawAsync(string method, string pathAndQuery, string? jsonBody, CancellationToken ct = default);
}

public sealed class ProviderDiagnostics : IProviderDiagnostics
{
    private const string ContextDevBase = "https://api.context.dev";

    private readonly IHttpClientFactory _http;
    private readonly IAgentFactory _agents;

    public bool Enabled { get; }

    public ProviderDiagnostics(IHttpClientFactory http, IAgentFactory agents, IConfiguration config)
    {
        _http = http;
        _agents = agents;
        Enabled = config.GetValue<bool>("DIAGNOSTICS_ENABLED");
    }

    public async Task<DiagResult> ContextDevRawAsync(
        string method, string pathAndQuery, string? jsonBody, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return new DiagResult(0, 0, string.Empty, "Diagnostics are disabled in this environment.");
        }

        var key = _agents.Resolve("CONTEXT_DEV_API_KEY");
        if (key is null)
        {
            return new DiagResult(0, 0, string.Empty, "CONTEXT_DEV_API_KEY is not configured on the server.");
        }

        var path = pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery;
        var verb = new HttpMethod(method.Trim().ToUpperInvariant());
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(40));

            var client = _http.CreateClient("diagnostics");
            using var req = new HttpRequestMessage(verb, ContextDevBase + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (!string.IsNullOrWhiteSpace(jsonBody) && verb != HttpMethod.Get)
            {
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();
            return new DiagResult((int)resp.StatusCode, sw.ElapsedMilliseconds, Truncate(body, 24_000), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancelled (e.g. page closed) — propagate; the local 40s timeout is still reported below
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagResult(0, sw.ElapsedMilliseconds, string.Empty, ex.Message);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n… [truncated, {s.Length} chars total]";
}
