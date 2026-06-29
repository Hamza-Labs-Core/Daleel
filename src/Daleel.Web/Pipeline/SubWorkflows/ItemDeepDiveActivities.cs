using System.Text.Json;
using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Identification;
using Daleel.Web.Profiles;
using Daleel.Web.Storage;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.Logging;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The steps of the per-item deep-dive sub-workflow. Specs are DB-first (a fresh saved ProductProfile is
// reused with no network); a thin item with an official/cheapest offer URL is scraped for authoritative,
// region-correct specs and saved for next time. Between scrape and price comparison the smart pipeline runs:
// identify the canonical brand model (text → vision), persist each source's raw specs to R2, merge/clean/
// normalize them into a canonical sheet, and save it. Price comparison + review collection run over the
// offers already aggregated upstream — no extra network for those.

/// <summary>Step 1 — find and scrape this item's detail page for specs (DB-first reuse).</summary>
[Activity("Daleel", "Item", "Scrape product pages: reuse a saved deep-dive or fetch official specs")]
public sealed class ScrapeProductPagesActivity : CancellableActivity
{
    /// <summary>An item with fewer than this many specs is "thin" and worth a scrape.</summary>
    private const int ThinSpecThreshold = 3;

    /// <summary>Cap on a saved detail blob (entity column is 8000).</summary>
    private const int MaxDetailChars = 4000;

    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var repo = context.GetRequiredService<IProductProfileRepository>();
        var options = context.GetRequiredService<ProfileOptions>();
        var ct = context.CancellationToken;

        state.Key = ProductProfile.KeyFor(state.Model.Brand, state.Model.Model, state.Model.Name);
        if (state.Key.Length == 0)
        {
            return; // not enough identity to key/scrape this item
        }

        // DB-first: a fresh saved deep-dive is reused with no network.
        var saved = await SafeGet(repo, state.Key, ct);
        if (saved is { } s && !s.IsStale(options.Now(), options.Ttl) && !string.IsNullOrWhiteSpace(s.Details))
        {
            state.Details = s.Details;
            state.ReusedFromCache = true;
            state.RecordEvent(EventCategory.Profile, "item.reuse", "profile",
                metadata: new Dictionary<string, object?> { ["item"] = state.Model.Name, ["cached"] = true });
            return;
        }

        // A thin item with an offer URL gets a fresh scrape of the brand's (or cheapest) page.
        var url = ItemEnrichmentService.OfficialOrCheapestUrl(state.Model);
        if (url is null || state.Model.Specs.Count >= ThinSpecThreshold)
        {
            return;
        }

        services.Report(SearchStep.ComparingPrices, "Progress.Msg.FetchingSpecs", state.Model.Name);
        var page = await services.Agent.ReadPageAsync(url, ct);
        if (page is not null && !string.IsNullOrWhiteSpace(page.Content))
        {
            state.Details = page.Content.Length <= MaxDetailChars ? page.Content : page.Content[..MaxDetailChars];
            state.SourceUrl = url;
            state.RecordEvent(EventCategory.Extract, "item.deepdive", "context.dev",
                metadata: new Dictionary<string, object?> { ["item"] = state.Model.Name, ["url"] = url });
        }
    }

    private static async Task<ProductProfile?> SafeGet(IProductProfileRepository repo, string key, CancellationToken ct)
    {
        try { return await repo.GetByKeyAsync(key, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}

/// <summary>Step 2 — merge the scraped/reused spec markdown into the item's specs.</summary>
[Activity("Daleel", "Item", "Extract specs: fold the scraped detail into the model's specs")]
public sealed class ExtractSpecsActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        if (string.IsNullOrWhiteSpace(state.Details))
        {
            return ValueTask.CompletedTask;
        }

        var specs = new Dictionary<string, string>(state.Result.Specs) { ["details"] = state.Details! };
        state.Result = state.Result with { Specs = specs };
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Step 3 — compare the item's prices across every store that offers it, and persist each priced offer as
/// a <see cref="ScrapedPrice"/> observation so the per-product price history/comparison reads it back
/// (the offers were aggregated upstream — this is the durable record of that comparison, not a new crawl).
/// </summary>
[Activity("Daleel", "Item", "Compare prices: aggregate the item's offers across stores")]
public sealed class ComparePricesActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var offers = state.Result.Offers;
        var stores = offers
            .Select(o => o.Source)
            .Where(src => !string.IsNullOrWhiteSpace(src))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        services.Report(SearchStep.ComparingPrices, "Progress.Msg.ComparingItemPrices", stores, state.Model.Name);

        var persisted = await PersistOfferPricesAsync(context, state, offers);
        state.RecordEvent(EventCategory.Extract, "item.compare", "pipeline",
            metadata: new Dictionary<string, object?>
            {
                ["item"] = state.Model.Name,
                ["stores"] = stores,
                ["offers"] = offers.Count,
                ["priced"] = persisted
            });
    }

    /// <summary>
    /// Writes one <see cref="ScrapedPrice"/> row per priced offer (keyed by the item's normalized product
    /// key so observations across stores collapse into one comparison) and returns how many were saved.
    /// Best-effort: a DB failure is logged and never fails the deep-dive.
    /// </summary>
    private static async Task<int> PersistOfferPricesAsync(
        ActivityExecutionContext context, ItemDeepDiveState state, IReadOnlyList<PriceOffer> offers)
    {
        // ScrapeProductPagesActivity (step 1) already computed the normalized brand+model key; without it
        // there's no stable identity to file the prices under.
        if (state.Key.Length == 0)
        {
            return 0;
        }

        var now = context.GetRequiredService<ProfileOptions>().Now();
        var rows = offers
            .Where(o => o.Price is not null && !string.IsNullOrWhiteSpace(o.Source))
            .Select(o => new ScrapedPrice
            {
                ProductName = state.Model.Name,
                ProductKey = state.Key,
                StoreName = o.Source,
                Price = o.Price,
                Currency = o.Currency,
                SourceUrl = o.Url,
                Provider = "pipeline",
                ScrapedAt = now
            })
            .ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        var repo = context.GetRequiredService<IScrapedPriceRepository>();
        var logger = context.GetRequiredService<ILogger<ComparePricesActivity>>();
        try
        {
            await repo.AddRangeAsync(rows, context.CancellationToken);
            return rows.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to persist {Count} compared prices for item {Item}", rows.Count, state.Model.Name);
            return 0;
        }
    }
}

/// <summary>Step 4 — record the user reviews/ratings already gathered for this item.</summary>
[Activity("Daleel", "Item", "Collect reviews: record the item's gathered reviews and ratings")]
public sealed class CollectReviewsActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var social = state.Result.BrandReputation?.Social;
        var reviewCount = social?.Reviews.Count ?? 0;
        var hasSummary = !string.IsNullOrWhiteSpace(state.Result.ReviewSummary);

        state.RecordEvent(EventCategory.Extract, "item.reviews", "pipeline",
            metadata: new Dictionary<string, object?>
            {
                ["item"] = state.Model.Name,
                ["reviews"] = reviewCount,
                ["hasSummary"] = hasSummary
            });
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Step 5 — persist the enriched item to the database so the dedicated product page (which reads ONLY
/// from saved data) can render it. Persists for <em>every</em> enriched item, not only freshly-scraped
/// ones: the canonical merged spec sheet is saved as <see cref="ProductProfile.SpecsJson"/> and any
/// scraped markdown as <see cref="ProductProfile.Details"/>. Without this an item that already had specs
/// (so no fresh scrape ran) was never written to the durable store, and its detail page showed the
/// "not yet available" placeholder despite the deep-dive having enriched it in-memory.
/// </summary>
[Activity("Daleel", "Item", "Save the enriched item profile to the database")]
public sealed class SaveItemProfileActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.Key.Length == 0)
        {
            return; // not enough identity to key a profile
        }

        // The canonical merged sheet is preferred; fall back to the item's as-extracted specs. The raw
        // markdown blob is archived under Details, never duplicated into the structured spec sheet.
        var specs = state.MergedSpecs is { Count: > 0 } merged
            ? new Dictionary<string, string>(merged)
            : new Dictionary<string, string>(state.Result.Specs);
        specs.Remove("details");
        var specsJson = specs.Count > 0 ? JsonSerializer.Serialize(specs) : null;

        // Nothing worth persisting: no fresh scrape AND no structured specs to save.
        if (string.IsNullOrWhiteSpace(state.Details) && specsJson is null)
        {
            return;
        }

        // A reused-from-cache item already has its Details persisted; only re-save when THIS run produced
        // canonical specs the saved row may predate (the coalescing upsert won't wipe the existing Details).
        if (state.ReusedFromCache && specsJson is null)
        {
            return;
        }

        var repo = context.GetRequiredService<IProductProfileRepository>();
        var options = context.GetRequiredService<ProfileOptions>();
        var logger = context.GetRequiredService<ILogger<SaveItemProfileActivity>>();
        await SafeUpsert(repo, new ProductProfile
        {
            Name = state.Model.Name,
            Brand = state.Model.Brand,
            Model = state.Model.Model,
            NameKey = state.Key,
            Details = state.Details,
            SpecsJson = specsJson,
            SourceUrl = state.SourceUrl,
            LastRefreshed = options.Now()
        }, logger, state.Model.Name, context.CancellationToken);
        services.Report(SearchStep.ComparingPrices, "Progress.Msg.SavedDeepDive", state.Model.Name);
    }

    private static async Task SafeUpsert(
        IProductProfileRepository repo, ProductProfile profile, ILogger logger, string item, CancellationToken ct)
    {
        try { await repo.UpsertAsync(profile, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // best-effort: saving a deep-dive must never fail the search, but the failure must be visible.
            logger.LogWarning(ex, "Failed to persist deep-dive ProductProfile for item {Item}", item);
        }
    }
}

/// <summary>
/// New step — identify which canonical brand model this (often vaguely-named) store listing is, via the
/// smart identifier (text match → cross-region discovery → cached vision match). Best-effort: an
/// unidentified item simply continues with its as-extracted specs.
/// </summary>
[Activity("Daleel", "Item", "Identify product: text/vision match against the brand-model database")]
public sealed class IdentifyProductActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var identifier = context.GetRequiredService<IProductIdentifier>();

        try
        {
            var id = await identifier.IdentifyAsync(state.Model, context.CancellationToken);
            if (!id.Matched)
            {
                return;
            }

            state.IdentifiedBrandModelId = id.BrandModelId;
            state.MatchConfidence = id.Confidence;
            state.MatchMethod = id.Method;
            state.Category = id.Category ?? state.Category;

            services.Report(SearchStep.ComparingPrices, "Progress.Msg.IdentifiedItem",
                state.Model.Name, id.CanonicalModelName, id.Method, id.Confidence.ToString("P0"));
            state.RecordEvent(EventCategory.Extract, "item.identify", id.Method == "vision" ? "openrouter" : "pipeline",
                metadata: new Dictionary<string, object?>
                {
                    ["item"] = state.Model.Name,
                    ["matched"] = id.CanonicalModelName,
                    ["method"] = id.Method,
                    ["confidence"] = Math.Round(id.Confidence, 2)
                });
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort: identification must never fail the deep-dive */ }
    }
}

/// <summary>
/// New step — persist each source's raw specs and the store product image, routing each to its own R2
/// bucket: structured store-listing / brand-site specs → the specs bucket (<c>raw/{brand}/{model}/</c>),
/// the raw scraped-detail dump → the data bucket (<c>site-data/{brand}/{model}/</c>), the product image →
/// the images bucket (<c>products/{brand}/{model}/</c>). Stages the structured sources for the merge. The
/// DB only ever stores R2 URLs, never the raw blobs.
/// </summary>
[Activity("Daleel", "Item", "Save raw specs: route each source's specs + image to its R2 bucket")]
public sealed class SaveRawSpecsActivity : CancellableActivity
{
    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var r2 = context.GetRequiredService<IR2StorageService>();
        var ct = context.CancellationToken;

        var brandKey = BrandModel.Normalize(state.Model.Brand ?? "unknown");
        var modelKey = BrandModel.Normalize(state.Model.Model ?? state.Model.Name);
        if (modelKey.Length == 0)
        {
            return;
        }

        // Raw structured specs → the specs bucket; the raw scraped-detail dump and product image route to
        // their own buckets (data / images). Each path keeps the {brand}/{model} folder layout.
        var modelPath = $"{brandKey}/{modelKey}";
        var dataPrefix = $"site-data/{modelPath}";

        // 1. Store-listing structured specs (the as-extracted specs, NOT the raw markdown dump).
        var store = new Dictionary<string, string>(state.Model.Specs);
        store.Remove("details");
        if (store.Count > 0)
        {
            state.RawSpecsBySource.Add(SpecSource.Store(store));
            await ItemSpecPipeline.SaveJson(r2, state, $"raw/{modelPath}/store-listing.json", store, R2Bucket.Specs, ct);
        }

        // 2. Brand-site specs from the identified catalogue model (the authoritative source).
        if (state.IdentifiedBrandModelId is { } id)
        {
            var models = context.GetRequiredService<IBrandModelRepository>();
            var bm = await ItemSpecPipeline.SafeGetModel(models, id, ct);
            state.Category ??= bm?.Category;

            var brandSpecs = ItemSpecPipeline.ParseSpecs(bm?.SpecsJson);
            if (brandSpecs.Count > 0)
            {
                state.RawSpecsBySource.Add(SpecSource.Brand(brandSpecs));
                await ItemSpecPipeline.SaveJson(r2, state, $"raw/{modelPath}/brand-site.json", brandSpecs, R2Bucket.Specs, ct);
            }
        }

        // 3. The raw scraped detail markdown — archived scraped data, never merged as a key-value spec.
        if (!string.IsNullOrWhiteSpace(state.Details))
        {
            var blob = JsonSerializer.Serialize(new { source = state.SourceUrl, content = state.Details });
            await ItemSpecPipeline.SaveJsonBlob(r2, state, $"{dataPrefix}/scraped-detail.json", blob, R2Bucket.Data, ct);
        }

        // 4. The store product image is kept as its original source URL (no download, no R2 upload).
        //    Result already carries Model.ImageUrl; the UI renders the external URL directly.

        if (state.RawSpecsR2Urls.Count > 0)
        {
            state.RecordEvent(EventCategory.Extract, "item.rawspecs", "r2",
                metadata: new Dictionary<string, object?>
                {
                    ["item"] = state.Model.Name,
                    ["sources"] = state.RawSpecsBySource.Count,
                    ["blobs"] = state.RawSpecsR2Urls.Count
                });
        }
    }
}

/// <summary>
/// New step — merge the staged sources into one canonical spec sheet: de-duplicate the same attribute
/// quoted by multiple sources, normalize units, resolve conflicts (brand site wins), and order/rename
/// against the category schema. Folds the clean sheet onto the result, replacing any raw dump.
/// </summary>
[Activity("Daleel", "Item", "Merge & clean specs: dedupe, normalize units, resolve conflicts")]
public sealed class MergeAndCleanSpecsActivity : CancellableActivity
{
    protected override ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.RawSpecsBySource.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var merger = context.GetRequiredService<ISpecMerger>();
        var merged = merger.Merge(state.RawSpecsBySource, state.Schema);
        if (merged.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        state.MergedSpecs = merged;
        // The canonical sheet is what the UI reads — replace the as-extracted specs with it.
        state.Result = state.Result with { Specs = new Dictionary<string, string>(merged) };

        services.Report(SearchStep.ComparingPrices, "Progress.Msg.MergedSpecs",
            state.RawSpecsBySource.Count, merged.Count, state.Model.Name);
        state.RecordEvent(EventCategory.Extract, "item.merge", "pipeline",
            metadata: new Dictionary<string, object?>
            {
                ["item"] = state.Model.Name,
                ["sources"] = state.RawSpecsBySource.Count,
                ["specs"] = merged.Count
            });
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// New step — persist the canonical spec sheet to <see cref="BrandModel.FinalSpecsJson"/> (when the item
/// was identified) and to R2 under <c>final-specs/{brand}/{model}.json</c>. This is the sheet the UI reads;
/// raw data never reaches the UI.
/// </summary>
[Activity("Daleel", "Item", "Save final specs: canonical sheet to DB + R2 (final-specs/)")]
public sealed class SaveFinalSpecsActivity : CancellableActivity
{
    /// <summary>The BrandModel.FinalSpecsJson column cap (mirrors the entity configuration).</summary>
    private const int MaxFinalSpecsChars = 8000;

    protected override async ValueTask DoExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.MergedSpecs is not { Count: > 0 } merged)
        {
            return;
        }

        var ct = context.CancellationToken;
        var json = JsonSerializer.Serialize(merged);

        var brandKey = BrandModel.Normalize(state.Model.Brand ?? "unknown");
        var modelKey = BrandModel.Normalize(state.Model.Model ?? state.Model.Name);
        if (modelKey.Length == 0)
        {
            return;
        }

        var r2 = context.GetRequiredService<IR2StorageService>();
        var logger = context.GetRequiredService<ILogger<SaveFinalSpecsActivity>>();
        state.FinalSpecsR2Url = await ItemSpecPipeline.SafeStoreJson(
            r2, $"final-specs/{brandKey}/{modelKey}.json", json, R2Bucket.Specs, ct);

        // Persist the canonical sheet onto a brand model so the model DB carries it directly. When the
        // listing matched an existing catalogue model we update that row; otherwise we CREATE the model
        // (a store-discovered item the catalogue crawler hadn't harvested yet) so its specs are still
        // queryable by brand/model and not lost just because there was no pre-existing match.
        if (json.Length <= MaxFinalSpecsChars)
        {
            if (state.IdentifiedBrandModelId is { } id)
            {
                var models = context.GetRequiredService<IBrandModelRepository>();
                try { await models.SaveFinalSpecsAsync(id, json, state.FinalSpecsR2Url, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // best-effort: the R2 copy + in-memory result remain authoritative.
                    logger.LogWarning(ex, "Failed to save final specs onto brand model {BrandModelId}", id);
                }
            }
            else
            {
                await CreateBrandModelAsync(context, state, json, logger, ct);
            }
        }

        services.Report(SearchStep.ComparingPrices, "Progress.Msg.SavedFinalSpecs",
            state.Model.Name, merged.Count);
        state.RecordEvent(EventCategory.Extract, "item.finalspecs", state.FinalSpecsR2Url is null ? "db" : "r2",
            metadata: new Dictionary<string, object?>
            {
                ["item"] = state.Model.Name,
                ["specs"] = merged.Count,
                ["brandModelId"] = state.IdentifiedBrandModelId
            });
    }

    /// <summary>
    /// Creates a <see cref="BrandModel"/> for an item that wasn't matched to a pre-existing catalogue row,
    /// get-or-creating its owning <see cref="Brand"/> first (a BrandModel must hang off a brand). Carries
    /// the canonical spec sheet so the model DB is queryable for store-discovered items the catalogue
    /// crawler hasn't harvested yet. Best-effort: needs a brand + model name to anchor the row, and any DB
    /// failure is logged rather than failing the deep-dive.
    /// </summary>
    private static async Task CreateBrandModelAsync(
        ActivityExecutionContext context, ItemDeepDiveState state, string json, ILogger logger, CancellationToken ct)
    {
        var brandName = state.Model.Brand;
        var modelName = string.IsNullOrWhiteSpace(state.Model.Model) ? state.Model.Name : state.Model.Model;
        if (string.IsNullOrWhiteSpace(brandName) || string.IsNullOrWhiteSpace(modelName))
        {
            // No brand (or no model name) to anchor a per-brand model row. The ProductProfile + R2 copy
            // already hold the specs, so the detail page still renders — we just skip the catalogue row.
            logger.LogDebug(
                "Skipping BrandModel creation for item {Item}: missing brand/model identity", state.Model.Name);
            return;
        }

        try
        {
            var now = context.GetRequiredService<ProfileOptions>().Now();
            var brands = context.GetRequiredService<IBrandRepository>();
            var brand = await brands.GetByNameAsync(brandName, ct)
                ?? await brands.UpsertAsync(
                    new Brand { Name = brandName, NameKey = Brand.Normalize(brandName), LastRefreshed = now }, ct);

            var models = context.GetRequiredService<IBrandModelRepository>();
            var saved = await models.UpsertAsync(new BrandModel
            {
                BrandId = brand.Id,
                ModelName = modelName,
                ModelKey = BrandModel.Normalize(modelName),
                Category = state.Category,
                ImageUrl = state.Model.ImageUrl,
                FinalSpecsJson = json,
                FinalSpecsR2Url = state.FinalSpecsR2Url,
                LastRefreshed = now,
                DiscoveredAt = now
            }, ct);
            // Reflect the freshly-created id so the recorded event reports the model it persisted to.
            state.IdentifiedBrandModelId = saved.Id;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create BrandModel for item {Item}", state.Model.Name);
        }
    }
}

/// <summary>Shared best-effort helpers for the spec-pipeline activities (R2 writes never fail the search).</summary>
internal static class ItemSpecPipeline
{
    /// <summary>Serializes a spec dictionary, uploads it to the given R2 bucket, and records the URL on the state.</summary>
    public static async Task SaveJson(
        IR2StorageService r2, ItemDeepDiveState state, string objectKey,
        IReadOnlyDictionary<string, string> specs, R2Bucket bucket, CancellationToken ct) =>
        await SaveJsonBlob(r2, state, objectKey, JsonSerializer.Serialize(specs), bucket, ct);

    /// <summary>Uploads a pre-serialized blob to the given R2 bucket and records the URL on the state.</summary>
    public static async Task SaveJsonBlob(
        IR2StorageService r2, ItemDeepDiveState state, string objectKey, string json,
        R2Bucket bucket, CancellationToken ct)
    {
        var url = await SafeStoreJson(r2, objectKey, json, bucket, ct);
        if (url is not null)
        {
            state.RawSpecsR2Urls.Add(url);
        }
    }

    public static async Task<string?> SafeStoreJson(
        IR2StorageService r2, string objectKey, string json, R2Bucket bucket, CancellationToken ct)
    {
        try { return await r2.StoreJsonAsync(json, objectKey, bucket, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static async Task<BrandModel?> SafeGetModel(IBrandModelRepository models, int id, CancellationToken ct)
    {
        try { return await models.GetByIdAsync(id, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Parses a BrandModel.SpecsJson object into a string→string dictionary (empty on failure).</summary>
    public static Dictionary<string, string> ParseSpecs(string? specsJson)
    {
        if (string.IsNullOrWhiteSpace(specsJson))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(specsJson) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
