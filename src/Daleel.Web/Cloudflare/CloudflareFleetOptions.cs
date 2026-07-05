using Microsoft.Extensions.Configuration;

namespace Daleel.Web.Cloudflare;

/// <summary>
/// One worker endpoint: base URL + an optional env-configured bearer. Under the token authority the
/// bearer normally comes from the credential vault per request (rotatable without a restart), so a
/// null Token here just means "no static fallback" — the URL alone configures the endpoint.
/// </summary>
public sealed record WorkerEndpoint(Uri BaseUrl, string? Token);

/// <summary>
/// Endpoints for the wider worker fleet (docs/architecture/cloudflare-workers-pipeline.md §3):
/// search (SerpAPI/Places caching proxy), classify, extract and filter hosts. Each is independently
/// optional — a null endpoint simply means that capability stays on its current inline path. The
/// scrape-worker keeps its own <see cref="CloudflareWorkerOptions"/> (it carries queue/drain state
/// the fleet endpoints don't have).
/// </summary>
public sealed record CloudflareFleetOptions
{
    public WorkerEndpoint? Search { get; init; }
    public WorkerEndpoint? Classify { get; init; }
    public WorkerEndpoint? Extract { get; init; }
    public WorkerEndpoint? Filter { get; init; }

    public bool AnyConfigured => Search is not null || Classify is not null || Extract is not null || Filter is not null;

    /// <summary>Reads CF_{SEARCH|CLASSIFY|EXTRACT|FILTER}_WORKER_URL/_TOKEN pairs; null when none are set.</summary>
    public static CloudflareFleetOptions? FromConfiguration(IConfiguration config)
    {
        var options = new CloudflareFleetOptions
        {
            Search = Endpoint(config, "CF_SEARCH_WORKER_URL", "CF_SEARCH_WORKER_TOKEN"),
            Classify = Endpoint(config, "CF_CLASSIFY_WORKER_URL", "CF_CLASSIFY_WORKER_TOKEN"),
            Extract = Endpoint(config, "CF_EXTRACT_WORKER_URL", "CF_EXTRACT_WORKER_TOKEN"),
            Filter = Endpoint(config, "CF_FILTER_WORKER_URL", "CF_FILTER_WORKER_TOKEN")
        };
        return options.AnyConfigured ? options : null;
    }

    private static WorkerEndpoint? Endpoint(IConfiguration config, string urlKey, string tokenKey)
    {
        // Only the URL is mandatory: the token authority serves bearers from the vault at request
        // time, so the _TOKEN env var is merely an optional static fallback. Requiring it here
        // would disable the whole endpoint on exactly the deployments that manage tokens
        // dynamically (which render the env token empty on purpose).
        var url = config[urlKey]?.Trim();
        var token = config[tokenKey]?.Trim();
        return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? new WorkerEndpoint(parsed, string.IsNullOrWhiteSpace(token) ? null : token)
            : null;
    }
}
