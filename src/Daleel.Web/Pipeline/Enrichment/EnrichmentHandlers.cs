using System.Text.RegularExpressions;
using Daleel.Agent;
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

        foreach (var (domain, storeName, entryUrl) in svc.SelectCatalogDomains(products))
        {
            // Extra attempts on purpose: the first ones may be spent politely waiting for the edge
            // drain to land this domain's rows before falling back to an inline crawl.
            children.Add(HandlerHelpers.Child(item, EnrichmentUnit.CatalogAttach,
                EnrichmentWorkQueue.Payload(new CatalogPayload(domain, storeName, entryUrl)), maxAttempts: 6));
        }

        foreach (var brand in svc.SelectBrandsForHarvest(products))
        {
            children.Add(HandlerHelpers.Child(item, EnrichmentUnit.BrandResearch,
                EnrichmentWorkQueue.Payload(new BrandPayload(brand)), maxAttempts: 4));
        }

        // Image lookups run late so the free sources (catalogues, brand DB) fill first — every image
        // they land is a paid SerpAPI lookup saved. Conditions run last for the same reason: they
        // read offers the other units attach.
        children.Add(HandlerHelpers.Child(item, EnrichmentUnit.ImageLookup, "{}", notBefore: TimeSpan.FromSeconds(90)));
        // Offers arrive with URLs but their facts are guesses — verify each offer AGAINST its own
        // page: relatedness, price, condition, description (the "you have the site, use it" rule).
        // After catalogs attach their offers.
        children.Add(HandlerHelpers.Child(item, EnrichmentUnit.PriceFetch, "{}", notBefore: TimeSpan.FromSeconds(120)));
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
        var (fromDrain, priced, drainCreated) = await svc.AttachScrapedPricesAsync(
            products.Models.ToList(), payload.Domain, payload.StoreName, ctx.Job.Query, ct);
        if (fromDrain is not null && (priced > 0 || drainCreated.Count > 0))
        {
            await Patch(ctx, item, fromDrain, ct);
            await EnqueueFollowUpsAsync(ctx, item, drainCreated, ct);
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

        var (inline, inlinePriced, inlineCreated) = await svc.AttachCatalogForDomainAsync(
            ctx.Agent(), products.Models.ToList(), payload.Domain, payload.StoreName,
            products.Geo, item.SearchJobId.ToString(), ctx.Job.Query, payload.EntryUrl, ct);
        if (inline is not null && inlinePriced >= 0)
        {
            await Patch(ctx, item, inline, ct);
            await EnqueueFollowUpsAsync(ctx, item, inlineCreated, ct);
        }

        return UnitOutcome.Ok;
    }

    /// <summary>
    /// Catalogue-discovered models arrive AFTER the plan fanned out, so they'd miss their spec dive
    /// and the (already-run) image pass — enqueue both. Queue recursion: discoveries create work.
    /// </summary>
    private static async Task EnqueueFollowUpsAsync(
        EnrichmentUnitContext ctx, EnrichmentWorkItem item, IReadOnlyList<string> created, CancellationToken ct)
    {
        if (created.Count == 0)
        {
            return;
        }

        var children = created
            .Select(name => HandlerHelpers.Child(item, EnrichmentUnit.ItemDive,
                EnrichmentWorkQueue.Payload(new ItemPayload(-1, name))))
            .ToList();
        children.Add(HandlerHelpers.Child(item, EnrichmentUnit.ImageLookup, "{}", notBefore: TimeSpan.FromSeconds(30)));
        await ctx.Queue.EnqueueAsync(children, ct);
    }

    private static Task Patch(
        EnrichmentUnitContext ctx, EnrichmentWorkItem item, List<ProductModel> models, CancellationToken ct) =>
        ctx.Results.PatchAsync(item,
            answer => answer.Products is null ? null : answer with { Products = answer.Products with { Models = models } },
            ct);
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
/// Verifies each offer AGAINST its own page: one fetch per offer URL yields four facts, applied in
/// one patch. (1) Relatedness gates everything — a page that isn't about the model removes the
/// offer entirely (a mismatched offer is worse than none). (2) Price, for offers still missing a
/// figure — exact when the priced line names the model, indicative otherwise (the "you have the
/// site, get the price" rule, unchanged). (3) Condition — the page's explicit used/refurbished/new
/// marker overrides whatever the listing guessed, and a "used"/"refurbished" the page doesn't
/// corroborate clears to null (secondhand needs positive evidence). (4) The model's "details"
/// prose, taken from its first offer's own page when the current blob is empty or junk — the
/// description comes from the site we actually list as the offer. Bounded batch per execution;
/// enqueues its own continuation while verifiable work remains (queue recursion, like the image
/// pass), and stops as soon as a batch changes nothing and no offer still awaits a figure.
/// </summary>
public sealed partial class OfferVerificationHandler : IEnrichmentUnitHandler
{
    /// <summary>Page fetches per execution — each is a render + parse, seconds apiece.</summary>
    private const int BatchSize = 6;

    /// <summary>The markdown's title region — where a product page names its own product.</summary>
    private const int TitleRegionChars = 600;

    /// <summary>How deep into the page an explicit condition marker is trusted.</summary>
    private const int ConditionScanChars = 1500;

    /// <summary>Readable characters below which a "details" blob counts as junk.</summary>
    private const int MinReadableChars = 120;

    /// <summary>Cap for a description lifted off an offer page.</summary>
    private const int DescriptionMaxChars = 2000;

    private readonly ILogger<OfferVerificationHandler> _logger;

    public OfferVerificationHandler(ILogger<OfferVerificationHandler> logger) => _logger = logger;

    public string Kind => EnrichmentUnit.PriceFetch;
    /// <summary>Everything one fetched page said, keyed by the model names it was judged for.</summary>
    private sealed record PageVerdict(
        HashSet<string> Judged,
        HashSet<string> Related,
        (decimal Price, string Currency, bool Exact)? Price,
        string? Condition,
        string? Description,
        string Content,
        string Url);

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products.Models: { Count: > 0 } models })
        {
            return UnitOutcome.Ok;
        }

        // ONE PAGE = ONE UNIT: this dispatcher only decides WHICH pages need verifying and enqueues
        // a VerifyPage unit per page. Each fetch then retries alone, fails alone, and runs as wide
        // as the consumer allows — no batches, no continuation chains (a chain once ran away live).
        var offerTargets = models
            .SelectMany(m => m.Offers.Select(o => (Model: m, o.Url)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Url) &&
                        NeedsVerification(x.Model, x.Model.Offers.First(o => o.Url == x.Url)))
            .GroupBy(x => x.Url!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Url: g.Key, Names: g.Select(x => x.Model.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FromMention: false));

        var offerUrls = new HashSet<string>(
            models.SelectMany(m => m.Offers).Select(o => o.Url).Where(u => !string.IsNullOrWhiteSpace(u))!,
            StringComparer.OrdinalIgnoreCase);
        var mentionTargets = models
            .Where(m => !m.Offers.Any(o => o.Price is not null))
            .SelectMany(m => m.Mentions
                .Where(link => !string.IsNullOrWhiteSpace(link.Url) && !offerUrls.Contains(link.Url))
                .Select(link => (Model: m, link.Url)))
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Url: g.Key, Names: g.Select(x => x.Model.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FromMention: true));

        // Never enqueue the same page twice for the same job — the queue itself is the ledger of
        // what's already been tried (any status counts: pending, running, done or dead).
        var db = ctx.Services.GetRequiredService<Daleel.Web.Data.DaleelDbContext>();
        var known = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.EnrichmentWorkItems
                    .Where(i => i.SearchJobId == item.SearchJobId && i.Kind == EnrichmentUnit.VerifyPage)
                    .Select(i => i.Payload), ct))
            .Select(payload => EnrichmentWorkQueue.ReadPayload<VerifyPagePayload>(payload)?.Url)
            .Where(u => u is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        var children = offerTargets.Concat(mentionTargets)
            .Where(x => !known.Contains(x.Url))
            .Select(x => HandlerHelpers.Child(item, EnrichmentUnit.VerifyPage,
                EnrichmentWorkQueue.Payload(new VerifyPagePayload(x.Url, x.Names, x.FromMention)),
                maxAttempts: 2))
            .ToList();

        if (children.Count > 0)
        {
            await ctx.Queue.EnqueueAsync(children, ct);
            // Offers keep arriving (catalogue units, mention discoveries): one more dispatch pass
            // later — and only when THIS pass found new pages, so an idle dispatch ends the chain.
            await ctx.Queue.EnqueueAsync(new[]
            {
                HandlerHelpers.Child(item, EnrichmentUnit.PriceFetch, "{}",
                    notBefore: TimeSpan.FromSeconds(120), maxAttempts: 1)
            }, ct);
            _logger.LogInformation(
                "Offer verification for job {JobId}: dispatched {Count} page unit(s)",
                item.SearchJobId, children.Count);
        }

        return UnitOutcome.Ok;
    }

    /// <summary>An offer's page is worth fetching when any of the four facts is still in doubt.</summary>    /// <summary>An offer's page is worth fetching when any of the four facts is still in doubt.</summary>
    private static bool NeedsVerification(ProductModel model, PriceOffer offer) =>
        offer.Price is null
        || IsSecondhand(offer.Condition)
        || (NeedsDetails(model) && IsFirstUrlOffer(model, offer));

    private static bool IsFirstUrlOffer(ProductModel model, PriceOffer offer) =>
        ReferenceEquals(model.Offers.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.Url)), offer);

    private static bool IsSecondhand(string? condition) =>
        string.Equals(condition, "used", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(condition, "refurbished", StringComparison.OrdinalIgnoreCase);

    internal static bool NeedsDetails(ProductModel model)
    {
        var details = model.Specs
            .FirstOrDefault(kv => kv.Key.Equals("details", StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(details) || IsJunkDetails(details);
    }

    /// <summary>
    /// The relatedness gate: the page must actually be about the model before any of its facts are
    /// trusted. Related when the title region (first 600 chars) shares ≥2 significant tokens with
    /// "{Brand} {Model} {Name}", or the page anywhere shares ≥3. Models with fewer than two
    /// significant tokens can't be judged — treated as related (never remove an offer on a test
    /// that couldn't run).
    /// </summary>
    internal static bool IsRelatedPage(string content, ProductModel model)
    {
        var want = SignificantTokens(model);
        if (want.Count < 2)
        {
            return true;
        }

        var head = content.Length <= TitleRegionChars ? content : content[..TitleRegionChars];
        if (want.Count(tok => head.Contains(tok, StringComparison.OrdinalIgnoreCase)) >= 2)
        {
            return true;
        }

        return want.Count(tok => content.Contains(tok, StringComparison.OrdinalIgnoreCase)) >= 3;
    }

    /// <summary>
    /// The page's explicit condition marker within its lead: used/مستعمل, refurbished/مجدد,
    /// new/جديد — null when the page is silent. Word-boundary matched so "unused" never reads as
    /// used; the Arabic terms are distinctive enough to match bare (clitic article prefixes like
    /// المستعمل still contain the word). Secondhand markers win over "new" — "new &amp; used"
    /// pages sell used units.
    /// </summary>
    internal static string? ExtractCondition(string content)
    {
        var lead = content.Length <= ConditionScanChars ? content : content[..ConditionScanChars];
        if (UsedRegex().IsMatch(lead) || lead.Contains("مستعمل", StringComparison.Ordinal))
        {
            return "used";
        }

        if (RefurbishedRegex().IsMatch(lead) || lead.Contains("مجدد", StringComparison.Ordinal))
        {
            return "refurbished";
        }

        if (NewRegex().IsMatch(lead) || lead.Contains("جديد", StringComparison.Ordinal))
        {
            return "new";
        }

        return null;
    }

    /// <summary>
    /// Page truth over listing guesses: an explicit page marker replaces the offer's condition
    /// outright; a silent page clears an unevidenced "used"/"refurbished" (secondhand claims need
    /// positive evidence) and leaves anything else as it was.
    /// </summary>
    internal static string? ApplyConditionEvidence(string? current, string? pageMarker) =>
        pageMarker ?? (IsSecondhand(current) ? null : current);

    /// <summary>
    /// The page's own product description: the first contiguous run of ≥2 kept-prose lines totaling
    /// ≥120 characters, joined and capped at 2000. Blank lines don't break a run (paragraph
    /// spacing); dropped navigation lines do — a menu between two lines means different blocks.
    /// </summary>
    internal static string? ExtractDescription(string content)
    {
        var run = new List<string>();
        foreach (var raw in content.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (CleanLine(raw) is { } prose)
            {
                run.Add(prose);
                continue;
            }

            if (Qualifies(run))
            {
                break;
            }

            run.Clear();
        }

        if (!Qualifies(run))
        {
            return null;
        }

        var text = string.Join("\n", run);
        return text.Length <= DescriptionMaxChars ? text : text[..DescriptionMaxChars].TrimEnd();

        static bool Qualifies(List<string> lines) =>
            lines.Count >= 2 && lines.Sum(l => l.Length) >= MinReadableChars;
    }

    /// <summary>Junk when fewer than 120 readable characters survive the line cleaning.</summary>
    internal static bool IsJunkDetails(string details) =>
        details.Split('\n').Sum(line => CleanLine(line)?.Length ?? 0) < MinReadableChars;

    /// <summary>
    /// One line of scraped markdown → readable prose, or null when the line is navigation-shaped.
    /// Minimal shared port of the detail panel's CleanScrapedProse: links collapse to their text,
    /// bare URLs vanish, and lines that were mostly links or read like menu words are dropped.
    /// </summary>
    private static string? CleanLine(string line)
    {
        var cleaned = ImageLinkRegex().Replace(line, " ");
        cleaned = MarkdownLinkRegex().Replace(cleaned, "$1");
        var beforeUrlStrip = cleaned;
        cleaned = BareUrlRegex().Replace(cleaned, " ");
        cleaned = cleaned.Replace("#", " ").Replace("*", " ").Trim();

        if (cleaned.Length < 3)
        {
            return null;
        }

        var linkHeavy = beforeUrlStrip.Length > 0 && cleaned.Length < beforeUrlStrip.Length / 2;
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var menuish = words.Length <= 6 && !cleaned.Contains('.') && !cleaned.Contains('،') &&
            words.All(w => w.Length <= 12);
        return linkHeavy || menuish ? null : cleaned;
    }

    /// <summary>
    /// The page's most credible price for the model: prefer the priced LINE sharing the most model
    /// tokens (exact); else the page's first price (a product page leads with its own — indicative).
    /// </summary>
    internal static (decimal Price, string Currency, bool Exact)? PickPrice(
        string content, ProductModel model)
    {
        var matches = Daleel.Core.Pricing.PriceParser.Extract(content);
        if (matches.Count == 0)
        {
            return null;
        }

        var want = SignificantTokens(model);
        var best = matches
            .Select(match => (Match: match, Shared: want.Count(tok =>
                match.Line.Contains(tok, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Shared)
            .First();

        return best.Shared >= 2
            ? (best.Match.Price, best.Match.Currency, Exact: true)
            : (matches[0].Price, matches[0].Currency, Exact: false);
    }

    /// <summary>The model's identity tokens (≥3 chars, lowercased) — shared by every check above.</summary>
    private static HashSet<string> SignificantTokens(ProductModel model) =>
        $"{model.Brand} {model.Model} {model.Name}"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tok => tok.Length >= 3)
            .Select(tok => tok.ToLowerInvariant())
            .ToHashSet();

    [GeneratedRegex(@"\bused\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsedRegex();

    [GeneratedRegex(@"\brefurbished\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RefurbishedRegex();

    [GeneratedRegex(@"\bnew\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageLinkRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex BareUrlRegex();
}

/// <summary>
/// ONE page's verification — the atom of the "you have the site, get the facts" rule. Fetches the
/// page once and applies everything it says: relatedness (an unrelated offer is REMOVED), a missing
/// price, condition truth (secondhand claims need on-page evidence), the model's description, an
/// offer CREATED when the page was reached via a mention, and sibling-SKU discovery (a 3-listing
/// page yields 3 distinct products). Retries alone; a dead page never costs any other unit.
/// </summary>
public sealed class VerifyPageHandler : IEnrichmentUnitHandler
{
    private readonly ILogger<VerifyPageHandler> _logger;

    public VerifyPageHandler(ILogger<VerifyPageHandler> logger) => _logger = logger;

    public string Kind => EnrichmentUnit.VerifyPage;
    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<VerifyPagePayload>(item.Payload) is not { } payload ||
            string.IsNullOrWhiteSpace(payload.Url))
        {
            return new UnitOutcome.Kill("unreadable verify-page payload");
        }

        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products.Models: { Count: > 0 } models })
        {
            return UnitOutcome.Ok;
        }

        var named = models
            .Where(m => payload.ModelNames.Contains(m.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (named.Count == 0)
        {
            return UnitOutcome.Ok; // the models this page was selected for no longer exist
        }

        var providers = ctx.Services.GetRequiredService<IProviderApi>();
        Daleel.Search.Abstractions.ScrapedPage? page;
        try
        {
            page = await providers.ScrapePageAsync(payload.Url, ct: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new UnitOutcome.Retry($"page fetch failed: {ex.Message}");
        }

        if (page is null || string.IsNullOrWhiteSpace(page.Content))
        {
            return new UnitOutcome.Retry("page fetch returned nothing");
        }

        var content = page.Content;
        var related = named
            .Where(m => OfferVerificationHandler.IsRelatedPage(content, m))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var price = related.Count > 0 ? OfferVerificationHandler.PickPrice(content, named[0]) : null;
        var condition = OfferVerificationHandler.ExtractCondition(content);
        var description = OfferVerificationHandler.ExtractDescription(content);

        await ctx.Results.PatchAsync(item, answer =>
        {
            if (answer.Products is not { } p)
            {
                return null;
            }

            var changed = false;
            var updated = p.Models.Select(m =>
            {
                if (!payload.ModelNames.Contains(m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    return m;
                }

                var modelChanged = false;
                var offers = new List<PriceOffer>(m.Offers.Count + 1);

                // A related, priced MENTION page becomes an offer the model didn't have.
                if (payload.FromMention && related.Contains(m.Name) && price is { } mp &&
                    !m.Offers.Any(o => o.Price is not null) &&
                    !m.Offers.Any(o => string.Equals(o.Url, payload.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    offers.Add(new PriceOffer
                    {
                        Source = AgentService.HostLabel(payload.Url) ?? "Store",
                        SourceType = Daleel.Core.Intelligence.ResultType.StorePage,
                        Price = mp.Price, Currency = mp.Currency, Url = payload.Url,
                        IsIndicative = !mp.Exact, IsLocal = true
                    });
                    modelChanged = true;
                }

                foreach (var o in m.Offers)
                {
                    if (!string.Equals(o.Url, payload.Url, StringComparison.OrdinalIgnoreCase))
                    {
                        offers.Add(o);
                        continue;
                    }

                    if (!related.Contains(m.Name))
                    {
                        modelChanged = true; // the page isn't about this model: drop the lie
                        continue;
                    }

                    var next = o;
                    if (next.Price is null && price is { } f)
                    {
                        next = next with { Price = f.Price, Currency = f.Currency, IsIndicative = !f.Exact };
                    }

                    var evidenced = OfferVerificationHandler.ApplyConditionEvidence(next.Condition, condition);
                    if (!string.Equals(evidenced, next.Condition, StringComparison.OrdinalIgnoreCase))
                    {
                        next = next with { Condition = evidenced };
                    }

                    modelChanged |= !ReferenceEquals(next, o);
                    offers.Add(next);
                }

                var result = modelChanged ? m with { Offers = offers } : m;

                if (related.Contains(m.Name) && description is { } prose &&
                    OfferVerificationHandler.NeedsDetails(result))
                {
                    var key = result.Specs.Keys.FirstOrDefault(
                        k => k.Equals("details", StringComparison.OrdinalIgnoreCase)) ?? "details";
                    var specs = result.Specs.ToDictionary(kv => kv.Key, kv => kv.Value);
                    specs[key] = prose;
                    result = result with { Specs = specs };
                    modelChanged = true;
                }

                changed |= modelChanged;
                return result;
            }).ToList();

            // Sibling SKUs: the page's OTHER priced lines become distinct products (variant-aware).
            if (related.Count > 0)
            {
                var lines = Daleel.Core.Pricing.PriceParser.Extract(content);
                if (lines.Count >= 2)
                {
                    var (withSiblings, created) = ItemEnrichmentService.AppendCatalogDiscoveries(
                        updated,
                        lines.Select(l => (Name: l.Line, (decimal?)l.Price, (string?)l.Currency,
                            (string?)payload.Url, (string?)null, Indicative: true)),
                        storeName: AgentService.HostLabel(payload.Url),
                        query: ctx.Job.Query, geo: ctx.Job.Geo);
                    if (created.Count > 0)
                    {
                        updated = withSiblings;
                        changed = true;
                        _logger.LogInformation(
                            "Verify page for job {JobId}: {Url} yielded {Count} sibling product(s)",
                            item.SearchJobId, payload.Url, created.Count);
                    }
                }
            }

            return changed ? answer with { Products = p with { Models = updated } } : null;
        }, ct);

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
