using System.Text;
using System.Text.Json;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Storage;

namespace Daleel.Web.Cloudflare;

/// <summary>
/// Drains the Cloudflare poll queue (doc §4.2): workers enqueue a thin pointer the moment an async
/// job's result is durable in R2, and this service — <em>not</em> the search workflow — reads the result
/// and persists it. That decoupling is the point: a catalogue crawl finishing after the search's
/// 10-minute deadline, after a cost-cap trip, or even after an app restart still lands in the
/// <see cref="ScrapedPrice"/> time series. Results are never lost to a timeout again.
/// </summary>
/// <remarks>
/// Delivery is at-least-once, so every handler is idempotent: a <c>.persisted</c> marker object is
/// written next to the result key, and a redelivered message that finds the marker just acks. The drain
/// runs whenever the queue credentials are configured — deliberately NOT gated on the
/// <c>cloudflare.execution.enabled</c> flag, so flipping the flag off never strands in-flight results.
/// </remarks>
public sealed class CloudflarePollDrainService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 10;
    private const int VisibilityTimeoutMs = 60_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IQueuePullClient _queue;
    private readonly ICloudflareWorkerClient _client;
    private readonly IR2StorageService _r2;
    private readonly ILogger<CloudflarePollDrainService> _logger;

    public CloudflarePollDrainService(
        IServiceScopeFactory scopeFactory,
        IQueuePullClient queue,
        ICloudflareWorkerClient client,
        IR2StorageService r2,
        ILogger<CloudflarePollDrainService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _client = client;
        _r2 = r2;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_queue.IsConfigured)
        {
            _logger.LogInformation(
                "Cloudflare poll drain idle: queue pull credentials not configured (CF_QUEUES_API_TOKEN / CF_POLL_QUEUE_ID)");
            return;
        }

        _logger.LogInformation("Cloudflare poll drain started");
        using var timer = new PeriodicTimer(TickInterval);
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await DrainOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The drain must survive any single bad tick — messages simply reappear after the
                // visibility timeout.
                _logger.LogWarning(ex, "Cloudflare poll drain tick failed");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>One pull → handle → ack/retry round. Internal for tests.</summary>
    internal async Task DrainOnceAsync(CancellationToken ct)
    {
        var messages = await _queue.PullAsync(BatchSize, VisibilityTimeoutMs, ct).ConfigureAwait(false);
        if (messages.Count == 0)
        {
            return;
        }

        var acks = new List<string>();
        var retries = new List<(string LeaseId, int DelaySeconds)>();

        foreach (var msg in messages)
        {
            var outcome = await HandleAsync(msg, ct).ConfigureAwait(false);
            if (outcome == Outcome.Ack)
            {
                acks.Add(msg.LeaseId);
            }
            else
            {
                // Explicit delay_seconds backoff (doc §4.3) — never rely on the visibility timeout.
                retries.Add((msg.LeaseId, Math.Min(60, 15 * Math.Max(1, msg.Attempts))));
            }
        }

        await _queue.AckAsync(acks, retries, ct).ConfigureAwait(false);
    }

    private enum Outcome { Ack, Retry }

    private async Task<Outcome> HandleAsync(PulledMessage raw, CancellationToken ct)
    {
        var msg = ParseBody(raw.Body);
        if (msg is null || string.IsNullOrWhiteSpace(msg.ResultKey))
        {
            _logger.LogWarning("Dropping unparseable poll message (lease {Lease})", raw.LeaseId);
            return Outcome.Ack; // a poison message must not loop forever
        }

        // Terminal worker failure: surface a real error on the timeline (faulted ≠ empty), then ack.
        // Same marker discipline as the success path so a redelivered failure doesn't spam the
        // timeline with duplicate warnings.
        if (!msg.IsDone)
        {
            var failMarker = msg.ResultKey + ".persisted";
            if (await _r2.ReadTextAsync(failMarker, 1024, R2Bucket.Data, ct).ConfigureAwait(false) is null)
            {
                await PublishEventAsync(msg, success: false,
                    summary: $"Edge {msg.Kind} job failed for {msg.Store ?? msg.Domain}: {msg.Error ?? "unknown error"}",
                    ct).ConfigureAwait(false);
                await _r2.StoreJsonAsync(
                    JsonSerializer.Serialize(new { failedAt = DateTimeOffset.UtcNow, msg.Error }),
                    failMarker, R2Bucket.Data, ct).ConfigureAwait(false);
            }
            return Outcome.Ack;
        }

        return msg.Kind?.ToLowerInvariant() switch
        {
            "catalog" => await PersistCatalogAsync(msg, msg.ResultKey!, ct).ConfigureAwait(false),
            "brand" => await PersistBrandAsync(msg, msg.ResultKey!, ct).ConfigureAwait(false),
            _ => AckUnknown(msg)
        };
    }

    private Outcome AckUnknown(PollMessage msg)
    {
        _logger.LogWarning("No drain handler for poll message kind '{Kind}' — acking", msg.Kind);
        return Outcome.Ack;
    }

    /// <summary>
    /// Persists a finished catalogue (or brand-with-catalogue) crawl into the ScrapedPrice time series —
    /// the same rows the inline ScrapePricesActivity writes, so the store page and per-product price
    /// comparison read worker results with zero changes.
    /// </summary>
    private async Task<Outcome> PersistCatalogAsync(PollMessage msg, string resultKey, CancellationToken ct)
    {
        var markerKey = resultKey + ".persisted";
        if (await _r2.ReadTextAsync(markerKey, 1024, R2Bucket.Data, ct).ConfigureAwait(false) is not null)
        {
            return Outcome.Ack; // redelivery of an already-persisted result
        }

        var doc = await _client.ReadResultAsync<CatalogResultDoc>(resultKey, ct).ConfigureAwait(false);
        if (doc is null)
        {
            // "done" with no readable result should be transient (or an oversized/invalid doc,
            // logged by the client); retry, and let the deadline decide when to give up — but a
            // give-up must be VISIBLE, never a silent ack (faulted ≠ empty), and marker-idempotent:
            // if the ack after this fails, the redelivered message must not re-publish the event.
            if (msg.DeadlineAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > msg.DeadlineAt)
            {
                await PublishEventAsync(msg, success: false,
                    summary: $"Edge {msg.Kind} result for {msg.Store ?? msg.Domain} could not be read before its " +
                             "deadline (missing, oversized, or invalid JSON) — crawl discarded",
                    ct).ConfigureAwait(false);
                await _r2.StoreJsonAsync(
                    JsonSerializer.Serialize(new { abandonedAt = DateTimeOffset.UtcNow, reason = "unreadable past deadline" }),
                    markerKey, R2Bucket.Data, ct).ConfigureAwait(false);
                return Outcome.Ack;
            }
            return Outcome.Retry;
        }

        var store = FirstNonEmpty(msg.Store, doc.Store, doc.Domain, msg.Domain) ?? "unknown-store";
        var rows = doc.Products
            .Where(p => p.Price is not null && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new ScrapedPrice
            {
                ProductName = p.Name,
                ProductKey = ProductProfile.KeyFor(null, null, p.Name),
                StoreName = store,
                Price = p.Price,
                Currency = p.Currency,
                SourceUrl = p.Url,
                Provider = "scrape-worker/context.dev",
                // The crawl's own capture time, not the drain time — the drain may run long after.
                ScrapedAt = doc.CapturedAt == default ? DateTimeOffset.UtcNow : doc.CapturedAt
            })
            .ToList();

        if (rows.Count > 0)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScrapedPriceRepository>();
            try
            {
                // Belt for the marker's crash window (rows inserted, marker write lost): a crawl is
                // identified by its capture instant, so rows for this store already carrying this
                // exact ScrapedAt mean a previous delivery persisted this very result — skip the
                // insert instead of duplicating the observations.
                var existing = await repo.LatestForStoreAsync(store, ct).ConfigureAwait(false);
                if (!existing.Any(r => r.ScrapedAt == rows[0].ScrapedAt))
                {
                    await repo.AddRangeAsync(rows, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Persisting {Count} drained price(s) for store {Store} failed; will retry", rows.Count, store);
                return Outcome.Retry;
            }
        }

        // Marker BEFORE ack: if the ack fails, the redelivered message short-circuits on the marker.
        await _r2.StoreJsonAsync(
            JsonSerializer.Serialize(new { persistedAt = DateTimeOffset.UtcNow, rows = rows.Count }),
            markerKey, R2Bucket.Data, ct).ConfigureAwait(false);

        await PublishEventAsync(msg, success: true,
            summary: $"Edge catalogue crawl for {store}: {doc.ProductCount} product(s), {rows.Count} priced",
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Drained edge {Kind} result for {Store}: {Total} product(s), {Priced} priced (job {JobId})",
            msg.Kind, store, doc.ProductCount, rows.Count, msg.JobId);
        return Outcome.Ack;
    }

    /// <summary>
    /// Persists a finished edge BRAND crawl (profile + catalogue) into the brand-model DB — the same
    /// <see cref="Data.BrandModel"/> rows the inline BrandCatalogService harvest writes, so brand
    /// pages and product identification read edge results with zero changes. The brand row must
    /// already exist (the submit happens from a path that owns it); an unknown brand is surfaced as
    /// a failure event, never guessed.
    /// </summary>
    private async Task<Outcome> PersistBrandAsync(PollMessage msg, string resultKey, CancellationToken ct)
    {
        var markerKey = resultKey + ".persisted";
        if (await _r2.ReadTextAsync(markerKey, 1024, R2Bucket.Data, ct).ConfigureAwait(false) is not null)
        {
            return Outcome.Ack; // redelivery of an already-persisted result
        }

        var doc = await _client.ReadResultAsync<CatalogResultDoc>(resultKey, ct).ConfigureAwait(false);
        if (doc is null)
        {
            if (msg.DeadlineAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > msg.DeadlineAt)
            {
                await PublishEventAsync(msg, success: false,
                    summary: $"Edge brand result for {msg.Store ?? msg.Domain} could not be read before its " +
                             "deadline — harvest discarded",
                    ct).ConfigureAwait(false);
                await _r2.StoreJsonAsync(
                    JsonSerializer.Serialize(new { abandonedAt = DateTimeOffset.UtcNow, reason = "unreadable past deadline" }),
                    markerKey, R2Bucket.Data, ct).ConfigureAwait(false);
                return Outcome.Ack;
            }
            return Outcome.Retry;
        }

        var brandName = FirstNonEmpty(msg.Store, doc.Store);
        var harvested = 0;
        using (var scope = _scopeFactory.CreateScope())
        {
            var brands = scope.ServiceProvider.GetRequiredService<Data.IBrandRepository>();
            var models = scope.ServiceProvider.GetRequiredService<Data.IBrandModelRepository>();
            var brand = brandName is null
                ? null
                : await SafeGetBrand(brands, brandName, ct).ConfigureAwait(false);
            if (brand is null)
            {
                await PublishEventAsync(msg, success: false,
                    summary: $"Edge brand harvest for '{brandName ?? msg.Domain}' had no matching brand row — models discarded",
                    ct).ConfigureAwait(false);
                await _r2.StoreJsonAsync(
                    JsonSerializer.Serialize(new { abandonedAt = DateTimeOffset.UtcNow, reason = "unknown brand" }),
                    markerKey, R2Bucket.Data, ct).ConfigureAwait(false);
                return Outcome.Ack;
            }

            try
            {
                foreach (var product in doc.Products.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                {
                    // Same mapping as BrandCatalogService.HarvestAsync — one canonical shape.
                    await models.UpsertAsync(new Data.BrandModel
                    {
                        BrandId = brand.Id,
                        ModelName = product.Name,
                        ModelKey = Data.BrandModel.Normalize(product.Name),
                        Category = product.Category,
                        SpecsJson = BuildBrandSpecs(product),
                        ImageUrl = string.IsNullOrWhiteSpace(product.ImageUrl) ? null : product.ImageUrl!.Trim(),
                        LocalPrice = product.Price,
                        Currency = product.Currency,
                        IsAvailable = true,
                        SourceUrl = product.Url,
                        LastRefreshed = doc.CapturedAt == default ? DateTimeOffset.UtcNow : doc.CapturedAt
                    }, ct).ConfigureAwait(false);
                    harvested++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Persisting drained brand models for {Brand} failed after {Count}; will retry",
                    brand.Name, harvested);
                return Outcome.Retry;
            }
        }

        await _r2.StoreJsonAsync(
            JsonSerializer.Serialize(new { persistedAt = DateTimeOffset.UtcNow, models = harvested }),
            markerKey, R2Bucket.Data, ct).ConfigureAwait(false);
        await PublishEventAsync(msg, success: true,
            summary: $"Edge brand harvest for {brandName}: {harvested} model(s) persisted",
            ct).ConfigureAwait(false);
        return Outcome.Ack;
    }

    private static async Task<Data.Brand?> SafeGetBrand(
        Data.IBrandRepository brands, string name, CancellationToken ct)
    {
        try { return await brands.GetByNameAsync(name, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Mirror of BrandCatalogService.BuildSpecs — description + sku into the specs blob.</summary>
    private static string? BuildBrandSpecs(Daleel.Search.Providers.CatalogProduct product)
    {
        var specs = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(product.Description))
        {
            specs["description"] = product.Description!;
        }
        if (!string.IsNullOrWhiteSpace(product.Sku))
        {
            specs["sku"] = product.Sku!;
        }
        return specs.Count == 0 ? null : JsonSerializer.Serialize(specs);
    }

    /// <summary>Timeline visibility for drained results (they run outside any workflow's event buffer).</summary>
    private async Task PublishEventAsync(PollMessage msg, bool success, string summary, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var log = scope.ServiceProvider.GetRequiredService<ISystemEventLog>();
            await log.PublishAsync(new SystemEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Category = SystemEventCategory.Store,
                EventType = success ? "store.prices.drained" : "store.prices.edge_failed",
                Severity = success ? SystemEventSeverity.Info : SystemEventSeverity.Warning,
                Source = "cloudflare/scrape-worker",
                Summary = summary,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    msg.JobId,
                    msg.ResultKey,
                    msg.Store,
                    msg.Domain,
                    msg.Error
                }),
                CorrelationId = msg.SearchJobId
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Publishing drain event failed (best-effort)");
        }
    }

    /// <summary>Queue bodies are JSON; tolerate base64-wrapped JSON from the pull API.</summary>
    internal static PollMessage? ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (TryParse(body, out var direct))
        {
            return direct;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(body.Trim()));
            return TryParse(decoded, out var fromBase64) ? fromBase64 : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool TryParse(string json, out PollMessage? msg)
    {
        try
        {
            msg = JsonSerializer.Deserialize<PollMessage>(json, CloudflareJson.Options);
            return msg is not null;
        }
        catch (JsonException)
        {
            msg = null;
            return false;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
