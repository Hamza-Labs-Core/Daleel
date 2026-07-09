using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Services;

/// <summary>Health of a single external API provider used by the search pipeline.</summary>
/// <param name="Name">Human-readable provider name (e.g. "Web search (SerpAPI)").</param>
/// <param name="Configured">Whether a key/credential for this provider is present on the server.</param>
/// <param name="Reachable">Whether the provider's host responded to a lightweight probe.</param>
/// <param name="LatencyMs">Round-trip time of the probe, in milliseconds.</param>
public sealed record ProviderHealth(string Name, bool Configured, bool Reachable, long LatencyMs);

/// <summary>A point-in-time snapshot of overall system health for the /status page.</summary>
public sealed record SystemStatus(
    bool SiteUp,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastSuccessfulSearch,
    IReadOnlyList<ProviderHealth> Providers)
{
    /// <summary>True when every configured provider is reachable.</summary>
    public bool AllOperational => Providers.Where(p => p.Configured).All(p => p.Reachable);
}

public interface IStatusService
{
    /// <summary>Probes provider reachability and reads the last successful search time.</summary>
    Task<SystemStatus> GetStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Builds the /status snapshot: pings each provider's host with a short-timeout HTTP probe (any HTTP
/// response — even 401/404 — counts as reachable, since it proves the host is up), reports whether a
/// key is configured for each, and looks up the most recent completed search job.
/// </summary>
public sealed class StatusService : IStatusService
{
    // host-reachability probes. We only care that the host answers, not the status code, so these point
    // at cheap public endpoints and treat any response as "up".
    private static readonly (string Name, string EnvKey, string Url)[] Probes =
    {
        // Display names are shown on the PUBLIC /status page — keep them capability-level, never naming the
        // underlying vendor. The EnvKey/Url stay internal (used only for the probe, not rendered).
        ("AI models", "OPENROUTER_API_KEY", "https://openrouter.ai/api/v1/models"),
        ("Web search", "SERPAPI_KEY", "https://serpapi.com/"),
        ("Store search", "GOOGLE_PLACES_API_KEY", "https://places.googleapis.com/"),
        ("Page reader", "CONTEXT_DEV_API_KEY", "https://api.context.dev/"),
        ("Browser render", "CLOUDFLARE_API_TOKEN", "https://api.cloudflare.com/client/v4/"),
        ("Social posts", "APIFY_TOKEN", "https://api.apify.com/v2/"),
    };

    private readonly IHttpClientFactory _http;
    private readonly IAgentFactory _agents;
    private readonly DaleelDbContext _db;

    public StatusService(IHttpClientFactory http, IAgentFactory agents, DaleelDbContext db)
    {
        _http = http;
        _agents = agents;
        _db = db;
    }

    public async Task<SystemStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var providerTasks = Probes.Select(p => ProbeAsync(p.Name, p.EnvKey, p.Url, ct)).ToArray();

        // The last completed search across the whole system — a strong "searches are working" signal.
        // Order by the auto-increment Id (monotonic with creation, so the newest completed job has the
        // highest Id) instead of CompletedAt: the identity key gives a stable, provider-agnostic
        // newest-first ordering.
        var lastSearch = await _db.SearchJobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Completed && j.CompletedAt != null)
            .OrderByDescending(j => j.Id)
            .Select(j => j.CompletedAt)
            .FirstOrDefaultAsync(ct);

        var providers = await Task.WhenAll(providerTasks);

        return new SystemStatus(
            SiteUp: true, // if this code runs, the web app is serving requests.
            CheckedAt: DateTimeOffset.UtcNow,
            LastSuccessfulSearch: lastSearch,
            Providers: providers);
    }

    private async Task<ProviderHealth> ProbeAsync(string name, string envKey, string url, CancellationToken ct)
    {
        var configured = _agents.Resolve(envKey) is not null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            var client = _http.CreateClient("status-probe");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // We don't read the body — headers are enough to know the host answered.
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();

            // Any HTTP response (including 4xx) means the host is reachable.
            return new ProviderHealth(name, configured, Reachable: true, sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            // Timeout, DNS failure, connection refused → the provider is unreachable from here.
            return new ProviderHealth(name, configured, Reachable: false, sw.ElapsedMilliseconds);
        }
    }
}
