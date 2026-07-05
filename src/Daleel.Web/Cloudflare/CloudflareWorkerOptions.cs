using Microsoft.Extensions.Configuration;

namespace Daleel.Web.Cloudflare;

/// <summary>
/// Connection settings for the Cloudflare execution layer (docs/architecture/cloudflare-workers-pipeline.md):
/// the scrape-worker endpoint the pipeline submits jobs to, and the Queues pull-consumer credentials the
/// poll-drain service uses to learn when results are ready. Follows the <c>R2Options.FromConfiguration</c>
/// convention: a static factory that returns null when the mandatory pieces are absent, so the app runs
/// exactly as before until the environment is configured.
/// </summary>
/// <remarks>
/// Endpoints/secrets live in the environment (rendered by deploy.yml); whether the pipeline actually USES
/// the workers is the separate admin-editable <c>cloudflare.execution.enabled</c> SystemConfig flag, so
/// rollback is a settings toggle, never a redeploy (strangler-fig, doc §6).
/// </remarks>
public sealed record CloudflareWorkerOptions
{
    /// <summary>SystemConfig key gating whether the pipeline routes work to the workers.</summary>
    public const string EnabledFlag = "cloudflare.execution.enabled";

    /// <summary>SystemConfig key for the catalogue-crawl product cap; 0 = uncapped (vendor ceiling).</summary>
    public const string CatalogMaxProductsKey = "cloudflare.catalog.max_products";

    /// <summary>Base URL of the scrape-worker, e.g. https://daleel-scrape-worker.example.workers.dev.</summary>
    public required Uri ScrapeWorkerUrl { get; init; }

    /// <summary>
    /// Optional env-configured bearer for the scrape-worker. Under the token authority the bearer
    /// normally comes from the credential vault per request; this is only the static fallback.
    /// </summary>
    public string? ScrapeWorkerToken { get; init; }

    /// <summary>Cloudflare account id (shared with the R2 configuration).</summary>
    public string? AccountId { get; init; }

    /// <summary>API token with queues_read + queues_write, for the pull consumer.</summary>
    public string? QueuesApiToken { get; init; }

    /// <summary>Queue ID (not name) of the poll queue the workers produce into.</summary>
    public string? PollQueueId { get; init; }

    /// <summary>True when the poll-drain loop has everything it needs to pull/ack.</summary>
    public bool CanDrainQueue =>
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(QueuesApiToken) &&
        !string.IsNullOrWhiteSpace(PollQueueId);

    /// <summary>
    /// Reads the settings from configuration/environment. Returns null unless the worker URL is
    /// present — the minimum needed to submit anything. The bearer is optional (the token authority
    /// serves it from the vault per request; the env var is only a static fallback), and queue
    /// settings are optional on top (submits still work; the drain service just reports itself
    /// unconfigured).
    /// </summary>
    public static CloudflareWorkerOptions? FromConfiguration(IConfiguration config)
    {
        var url = config["CF_SCRAPE_WORKER_URL"]?.Trim();
        var token = config["CF_SCRAPE_WORKER_TOKEN"]?.Trim();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        return new CloudflareWorkerOptions
        {
            ScrapeWorkerUrl = parsed,
            ScrapeWorkerToken = string.IsNullOrWhiteSpace(token) ? null : token,
            AccountId = config["CLOUDFLARE_ACCOUNT_ID"]?.Trim(),
            QueuesApiToken = config["CF_QUEUES_API_TOKEN"]?.Trim(),
            PollQueueId = config["CF_POLL_QUEUE_ID"]?.Trim()
        };
    }
}
