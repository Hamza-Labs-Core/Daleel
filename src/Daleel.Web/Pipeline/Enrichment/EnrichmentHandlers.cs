using Daleel.Core.Models;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Services;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// Shared helpers for unit handlers: payload access and the item-locate/replace patch shape.
/// </summary>
internal static class HandlerHelpers
{
    /// <summary>
    /// Finds the payload's item in the CURRENT models list: the index is a hint, the name is the
    /// check — vision identification renames models, and patches from concurrent units reorder
    /// nothing but may have changed the item in place since this unit was enqueued.
    /// </summary>
    public static int Locate(IReadOnlyList<ProductModel> models, ItemPayload payload)
    {
        if (payload.Index >= 0 && payload.Index < models.Count &&
            string.Equals(models[payload.Index].Name, payload.Name, StringComparison.OrdinalIgnoreCase))
        {
            return payload.Index;
        }

        for (var i = 0; i < models.Count; i++)
        {
            if (string.Equals(models[i].Name, payload.Name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public static EnrichmentWorkItem Child(EnrichmentWorkItem parent, string kind, string payload,
        TimeSpan? notBefore = null, int maxAttempts = 4) => new()
    {
        SearchJobId = parent.SearchJobId,
        UserId = parent.UserId,
        HistoryEntryId = parent.HistoryEntryId,
        ResultType = parent.ResultType,
        Kind = kind,
        Payload = payload,
        MaxAttempts = maxAttempts,
        NotBefore = notBefore is { } delay ? DateTimeOffset.UtcNow + delay : default
    };
}

/// <summary>
/// The fan-out unit: runs the cheap brand-DB fill inline (no network), then enqueues every other
/// unit — one per item, per store domain, per brand — plus the deliberately-late job-level passes.
/// This is all the "enrich" step of the workflow does now: results arrive as each unit lands.
/// </summary>
public sealed class PlanEnrichmentHandler : IEnrichmentUnitHandler
{
    public string Kind => EnrichmentUnit.Plan;
    public TimeSpan Budget => TimeSpan.FromSeconds(60);

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products: { Models.Count: > 0 } products })
        {
            return UnitOutcome.Ok; // nothing to enrich (non-product answer)
        }

        var svc = ctx.Services.GetRequiredService<IItemEnrichmentService>();

        // Phase 0 inline: pure DB reads, fills images/prices/specs harvested by previous searches.
        if (await svc.FillFromBrandDatabaseUnitAsync(products.Models.ToList(), products.Geo, ct) is { } filled)
        {
            await ctx.Results.PatchAsync(item,
                answer => answer.Products is null ? null : answer with { Products = answer.Products with { Models = filled } },
                ct);
        }

        var children = new List<EnrichmentWorkItem>
        {
            HandlerHelpers.Child(item, EnrichmentUnit.Vision, "{}")
        };

        var head = Math.Min(PipelineLimits.MaxItems, products.Models.Count);
        for (var i = 0; i < head; i++)
        {
            children.Add(HandlerHelpers.Child(item, EnrichmentUnit.ItemDive,
                EnrichmentWorkQueue.Payload(new ItemPayload(i, products.Models[i].Name))));
        }

        foreach (var (domain, storeName) in svc.SelectCatalogDomains(products))
        {
            // Extra attempts on purpose: the first ones may be spent politely waiting for the edge
            // drain to land this domain's rows before falling back to an inline crawl.
            children.Add(HandlerHelpers.Child(item, EnrichmentUnit.CatalogAttach,
                EnrichmentWorkQueue.Payload(new CatalogPayload(domain, storeName)), maxAttempts: 6));
        }

        foreach (var brand in svc.SelectBrandsForHarvest(products))
        {
            children.Add(HandlerHelpers.Child(item, EnrichmentUnit.BrandHarvest,
                EnrichmentWorkQueue.Payload(new BrandPayload(brand))));
        }

        // Image lookups run late so the free sources (catalogues, brand DB) fill first — every image
        // they land is a paid SerpAPI lookup saved. Conditions run last for the same reason: they
        // read offers the other units attach.
        children.Add(HandlerHelpers.Child(item, EnrichmentUnit.ImageLookup, "{}", notBefore: TimeSpan.FromSeconds(90)));
        children.Add(HandlerHelpers.Child(item, EnrichmentUnit.Conditions, "{}", notBefore: TimeSpan.FromSeconds(150)));
        // Last on purpose: it prunes the offers every other unit attached, so it must see them all.
        children.Add(HandlerHelpers.Child(item, EnrichmentUnit.Reachability, "{}", notBefore: TimeSpan.FromSeconds(240)));

        await ctx.Queue.EnqueueAsync(children, ct);
        return UnitOutcome.Ok;
    }
}

/// <summary>One item's spec deep-dive: fresh-profile reuse or one official-page scrape + upsert.</summary>
public sealed class ItemDiveHandler : IEnrichmentUnitHandler
{
    public string Kind => EnrichmentUnit.ItemDive;
    public TimeSpan Budget => TimeSpan.FromSeconds(90);

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<ItemPayload>(item.Payload) is not { } payload)
        {
            return new UnitOutcome.Kill("unreadable item payload");
        }

        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products.Models: { } models })
        {
            return UnitOutcome.Ok;
        }

        var index = HandlerHelpers.Locate(models, payload);
        if (index < 0)
        {
            return UnitOutcome.Ok; // item no longer in the result (superseded by a rename/merge)
        }

        var svc = ctx.Services.GetRequiredService<IItemEnrichmentService>();
        if (await svc.DeepDiveItemAsync(ctx.Agent(), models[index], ct) is not { } dived)
        {
            return UnitOutcome.Ok;
        }

        await ctx.Results.PatchAsync(item, answer =>
        {
            if (answer.Products is not { } p)
            {
                return null;
            }

            var current = p.Models.ToList();
            var at = HandlerHelpers.Locate(current, payload);
            if (at < 0)
            {
                return null;
            }

            current[at] = dived;
            return answer with { Products = p with { Models = current } };
        }, ct);
        return UnitOutcome.Ok;
    }
}

/// <summary>Vision identification pass (internally capped + budgeted by the enrichment service).</summary>
public sealed class VisionUnitHandler : IEnrichmentUnitHandler
{
    public string Kind => EnrichmentUnit.Vision;
    public TimeSpan Budget => TimeSpan.FromSeconds(240);

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products: { Models.Count: > 0 } products })
        {
            return UnitOutcome.Ok;
        }

        var svc = ctx.Services.GetRequiredService<IItemEnrichmentService>();
        if (await svc.IdentifyViaVisionUnitAsync(products.Models.ToList(), products.Geo, ct) is not { } identified)
        {
            return UnitOutcome.Ok;
        }

        await ctx.Results.PatchAsync(item,
            answer => answer.Products is null ? null : answer with { Products = answer.Products with { Models = identified } },
            ct);
        return UnitOutcome.Ok;
    }
}

/// <summary>
/// One store domain's catalogue attach. Drain-aware: prices the edge scrape-worker already landed in
/// <c>ScrapedPrices</c> are matched FIRST (no vendor spend, no double-crawl); when the edge is on and
/// nothing has landed yet, the unit politely waits via retry — the queue, not a timeout, mediates
/// the race with the drain. Only after those attempts does it crawl inline as a fallback.
/// </summary>
public sealed class CatalogAttachHandler : IEnrichmentUnitHandler
{
    /// <summary>Attempts spent waiting on the edge drain before crawling inline.</summary>
    private const int DrainWaitAttempts = 3;

    public string Kind => EnrichmentUnit.CatalogAttach;
    public TimeSpan Budget => TimeSpan.FromSeconds(120);

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<CatalogPayload>(item.Payload) is not { } payload)
        {
            return new UnitOutcome.Kill("unreadable catalog payload");
        }

        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products: { Models.Count: > 0 } products })
        {
            return UnitOutcome.Ok;
        }

        var svc = ctx.Services.GetRequiredService<IItemEnrichmentService>();

        // Edge-first: match whatever the drain has persisted for this domain — the same data an
        // inline crawl would fetch, already paid for once on the edge.
        var (fromDrain, priced) = await svc.AttachScrapedPricesAsync(
            products.Models.ToList(), payload.Domain, payload.StoreName, ct);
        if (fromDrain is not null && priced > 0)
        {
            await Patch(ctx, item, fromDrain, ct);
            return UnitOutcome.Ok;
        }

        var providers = ctx.Services.GetRequiredService<IProviderApi>();
        var config = ctx.Services.GetRequiredService<ISystemConfigService>();
        var edgeActive = providers.HasEdge &&
            await config.GetBoolAsync(Cloudflare.CloudflareWorkerOptions.EnabledFlag, false, ct);
        if (edgeActive && item.Attempts <= DrainWaitAttempts)
        {
            return new UnitOutcome.Retry(
                $"awaiting edge drain for {payload.Domain}", TimeSpan.FromSeconds(45));
        }

        var (inline, inlinePriced) = await svc.AttachCatalogForDomainAsync(
            ctx.Agent(), products.Models.ToList(), payload.Domain, payload.StoreName,
            products.Geo, item.SearchJobId.ToString(), ct);
        if (inline is not null && inlinePriced >= 0)
        {
            await Patch(ctx, item, inline, ct);
        }

        return UnitOutcome.Ok;
    }

    private static Task Patch(
        EnrichmentUnitContext ctx, EnrichmentWorkItem item, List<ProductModel> models, CancellationToken ct) =>
        ctx.Results.PatchAsync(item,
            answer => answer.Products is null ? null : answer with { Products = answer.Products with { Models = models } },
            ct);
}

/// <summary>One brand's site harvest into the BrandModel DB + immediate refill of this result.</summary>
public sealed class BrandHarvestHandler : IEnrichmentUnitHandler
{
    public string Kind => EnrichmentUnit.BrandHarvest;
    public TimeSpan Budget => TimeSpan.FromSeconds(300);

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<BrandPayload>(item.Payload) is not { } payload)
        {
            return new UnitOutcome.Kill("unreadable brand payload");
        }

        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products: { Models.Count: > 0 } products })
        {
            return UnitOutcome.Ok;
        }

        var svc = ctx.Services.GetRequiredService<IItemEnrichmentService>();
        if (await svc.HarvestBrandAndRefillAsync(
                ctx.Agent(), payload.Brand, products.Models.ToList(), products.Geo,
                item.SearchJobId.ToString(), ct) is not { } refilled)
        {
            return UnitOutcome.Ok;
        }

        await ctx.Results.PatchAsync(item,
            answer => answer.Products is null ? null : answer with { Products = answer.Products with { Models = refilled } },
            ct);
        return UnitOutcome.Ok;
    }
}

/// <summary>
/// Paid image lookups for whatever is STILL imageless after the free sources ran. Each execution
/// spends a bounded batch; when more imageless items remain it enqueues its own continuation —
/// coverage is unbounded through the queue, while any single attempt stays small and retryable.
/// </summary>
public sealed class ImageLookupHandler : IEnrichmentUnitHandler
{
    /// <summary>Paid lookups per execution (~one grid page); the continuation covers the rest.</summary>
    private const int BatchSize = 12;

    public string Kind => EnrichmentUnit.ImageLookup;
    public TimeSpan Budget => TimeSpan.FromSeconds(180);

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products.Models: { Count: > 0 } models })
        {
            return UnitOutcome.Ok;
        }

        var svc = ctx.Services.GetRequiredService<IItemEnrichmentService>();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var attempts = 0;
        var remaining = false;
        foreach (var model in models)
        {
            if (!string.IsNullOrWhiteSpace(model.ImageUrl))
            {
                continue;
            }

            if (attempts >= BatchSize)
            {
                remaining = true;
                break;
            }

            attempts++;
            if (await svc.FindImageForItemAsync(ctx.Agent(), model, ct) is { } image)
            {
                found[model.Name] = image;
            }
        }

        if (found.Count > 0)
        {
            await ctx.Results.PatchAsync(item, answer =>
            {
                if (answer.Products is not { } p)
                {
                    return null;
                }

                var current = p.Models.ToList();
                var changed = false;
                for (var i = 0; i < current.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(current[i].ImageUrl) &&
                        found.TryGetValue(current[i].Name, out var image))
                    {
                        current[i] = current[i] with { ImageUrl = image };
                        changed = true;
                    }
                }

                return changed ? answer with { Products = p with { Models = current } } : null;
            }, ct);
        }

        if (remaining)
        {
            await ctx.Queue.EnqueueAsync(new[]
            {
                HandlerHelpers.Child(item, EnrichmentUnit.ImageLookup, "{}", notBefore: TimeSpan.FromSeconds(30))
            }, ct);
        }

        return UnitOutcome.Ok;
    }
}

/// <summary>Classify-worker condition backfill over the whole result (one batched edge call).</summary>
public sealed class ConditionsHandler : IEnrichmentUnitHandler
{
    public string Kind => EnrichmentUnit.Conditions;
    public TimeSpan Budget => TimeSpan.FromSeconds(60);

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        var providers = ctx.Services.GetRequiredService<IProviderApi>();
        if (!providers.HasEdgeClassify)
        {
            return UnitOutcome.Ok; // capability not configured — nothing to do, not an error
        }

        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products: { Models.Count: > 0 } products })
        {
            return UnitOutcome.Ok;
        }

        var svc = ctx.Services.GetRequiredService<IItemEnrichmentService>();
        if (await svc.BackfillConditionsUnitAsync(products.Models.ToList(), ct) is not { } labeled)
        {
            return UnitOutcome.Ok;
        }

        await ctx.Results.PatchAsync(item,
            answer => answer.Products is null ? null : answer with { Products = answer.Products with { Models = labeled } },
            ct);
        return UnitOutcome.Ok;
    }
}

/// <summary>
/// Prunes offers whose sites a user can't actually open — dead DNS, refused connections, gone
/// pages (the "clicked View and the site is blocked/dead" complaint). Conservative by design: the
/// probe treats bot-defenses as reachable, and a model that loses every offer stays in the grid as
/// an informational card — evidence of the product's existence isn't invalidated by a dead shop.
/// </summary>
public sealed class ReachabilityHandler : IEnrichmentUnitHandler
{
    /// <summary>Distinct offer URLs probed per execution — hosts are cached, so this is generous.</summary>
    private const int MaxProbes = 40;

    private readonly IReachabilityProbe _probe;
    private readonly ILogger<ReachabilityHandler> _logger;

    public ReachabilityHandler(IReachabilityProbe probe, ILogger<ReachabilityHandler> logger)
    {
        _probe = probe;
        _logger = logger;
    }

    public string Kind => EnrichmentUnit.Reachability;
    public TimeSpan Budget => TimeSpan.FromSeconds(120);

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products.Models: { Count: > 0 } models })
        {
            return UnitOutcome.Ok;
        }

        var urls = models.SelectMany(m => m.Offers)
            .Select(o => o.Url)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxProbes)
            .ToList();
        if (urls.Count == 0)
        {
            return UnitOutcome.Ok;
        }

        var verdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // Bounded fan-out: hosts repeat across offers and the probe caches per host, so this stays
        // a handful of real network calls even on a big grid.
        foreach (var chunk in urls.Chunk(8))
        {
            var results = await Task.WhenAll(chunk.Select(async u => (Url: u!, Ok: await _probe.IsReachableAsync(u!, ct))));
            foreach (var (url, ok) in results)
            {
                verdicts[url] = ok;
            }
        }

        var deadUrls = verdicts.Where(v => !v.Value).Select(v => v.Key).ToList();
        if (deadUrls.Count == 0)
        {
            return UnitOutcome.Ok;
        }

        _logger.LogInformation(
            "Reachability for job {JobId}: pruning {Dead}/{Total} offer url(s) users can't open",
            item.SearchJobId, deadUrls.Count, verdicts.Count);

        await ctx.Results.PatchAsync(item, answer =>
        {
            if (answer.Products is not { } p)
            {
                return null;
            }

            var changed = false;
            var pruned = p.Models.Select(m =>
            {
                var keep = m.Offers
                    .Where(o => string.IsNullOrWhiteSpace(o.Url) ||
                                !verdicts.TryGetValue(o.Url!, out var ok) || ok)
                    .ToList();
                if (keep.Count == m.Offers.Count)
                {
                    return m;
                }

                changed = true;
                return m with { Offers = keep };
            }).ToList();

            return changed ? answer with { Products = p with { Models = pruned } } : null;
        }, ct);
        return UnitOutcome.Ok;
    }
}

/// <summary>
/// The smart-cache gap refill (ServeAndEnrich) as one durable unit: wraps the runner's targeted
/// re-enrichment — thin items, deficient brands/stores — with the queue's retry semantics instead
/// of a fire-and-forget task under a watchdog.
/// </summary>
public sealed class CacheGapRefillHandler : IEnrichmentUnitHandler
{
    public string Kind => EnrichmentUnit.CacheGapRefill;
    public TimeSpan Budget => TimeSpan.FromSeconds(480);

    private readonly ILogger<CacheGapRefillHandler> _logger;

    public CacheGapRefillHandler(ILogger<CacheGapRefillHandler> logger) => _logger = logger;

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        var payload = EnrichmentWorkQueue.ReadPayload<PlanPayload>(item.Payload);
        if (payload?.QualityJson is null ||
            System.Text.Json.JsonSerializer.Deserialize<CacheQualityReport>(payload.QualityJson) is not { } quality)
        {
            return new UnitOutcome.Kill("unreadable cache-quality payload");
        }

        var db = ctx.Services.GetRequiredService<DaleelDbContext>();
        var job = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(db.SearchJobs, j => j.Id == item.SearchJobId, ct);
        if (job?.ResultJson is null)
        {
            return new UnitOutcome.Kill("search job/result no longer exists");
        }

        var baseResult = new SearchRunResult(
            job.ResultJson, item.ResultType, payload.FilteredCount, payload.FilteredCategories)
        {
            CacheQuality = quality
        };

        var runner = ctx.Services.GetRequiredService<ISearchRunner>();
        void Progress(string message) =>
            _logger.LogDebug("Re-enrich job {JobId}: {Message}", item.SearchJobId, message);

        var refilled = await runner.ReEnrichAsync(job, baseResult, quality, Progress, ct);
        if (refilled is null)
        {
            return UnitOutcome.Ok;
        }

        await ctx.Results.PatchAsync(item,
            _ => ResultSerialization.Deserialize<Daleel.Agent.AgentAnswer>(refilled.ResultJson),
            ct);
        return UnitOutcome.Ok;
    }
}
