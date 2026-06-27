using System.Text.Json;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Identification;
using Daleel.Web.Profiles;
using Daleel.Web.Storage;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The steps of the per-item deep-dive sub-workflow. Specs are DB-first (a fresh saved ProductProfile is
// reused with no network); a thin item with an official/cheapest offer URL is scraped for authoritative,
// region-correct specs and saved for next time. Between scrape and price comparison the smart pipeline runs:
// identify the canonical brand model (text → vision), persist each source's raw specs to R2, merge/clean/
// normalize them into a canonical sheet, and save it. Price comparison + review collection run over the
// offers already aggregated upstream — no extra network for those.

/// <summary>Step 1 — find and scrape this item's detail page for specs (DB-first reuse).</summary>
[Activity("Daleel", "Item", "Scrape product pages: reuse a saved deep-dive or fetch official specs")]
public sealed class ScrapeProductPagesActivity : CodeActivity
{
    /// <summary>An item with fewer than this many specs is "thin" and worth a scrape.</summary>
    private const int ThinSpecThreshold = 3;

    /// <summary>Cap on a saved detail blob (entity column is 8000).</summary>
    private const int MaxDetailChars = 4000;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
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

        services.Log($"Fetching official specs for {state.Model.Name}…");
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
public sealed class ExtractSpecsActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
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

/// <summary>Step 3 — compare the item's prices across every store that offers it (in-memory).</summary>
[Activity("Daleel", "Item", "Compare prices: aggregate the item's offers across stores")]
public sealed class ComparePricesActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        var offers = state.Result.Offers;
        var stores = offers
            .Select(o => o.Source)
            .Where(src => !string.IsNullOrWhiteSpace(src))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        services.Log($"Comparing {stores} store price(s) for {state.Model.Name}…");
        state.RecordEvent(EventCategory.Extract, "item.compare", "pipeline",
            metadata: new Dictionary<string, object?>
            {
                ["item"] = state.Model.Name,
                ["stores"] = stores,
                ["offers"] = offers.Count
            });
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 4 — record the user reviews/ratings already gathered for this item.</summary>
[Activity("Daleel", "Item", "Collect reviews: record the item's gathered reviews and ratings")]
public sealed class CollectReviewsActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
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

/// <summary>Step 5 — persist a freshly-scraped deep-dive so the next search reuses it.</summary>
[Activity("Daleel", "Item", "Save the enriched item profile to the database")]
public sealed class SaveItemProfileActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<ItemDeepDiveState>();
        var services = context.GetRequiredService<SubWorkflowServices>();
        if (state.ReusedFromCache || string.IsNullOrWhiteSpace(state.Details) || state.Key.Length == 0)
        {
            return; // reused from cache or nothing fresh to persist
        }

        var repo = context.GetRequiredService<IProductProfileRepository>();
        var options = context.GetRequiredService<ProfileOptions>();
        await SafeUpsert(repo, new ProductProfile
        {
            Name = state.Model.Name,
            Brand = state.Model.Brand,
            Model = state.Model.Model,
            NameKey = state.Key,
            Details = state.Details,
            SourceUrl = state.SourceUrl,
            LastRefreshed = options.Now()
        }, context.CancellationToken);
        services.Log($"Saved deep-dive for {state.Model.Name}.");
    }

    private static async Task SafeUpsert(IProductProfileRepository repo, ProductProfile profile, CancellationToken ct)
    {
        try { await repo.UpsertAsync(profile, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort: saving a deep-dive must never fail the search */ }
    }
}

/// <summary>
/// New step — identify which canonical brand model this (often vaguely-named) store listing is, via the
/// smart identifier (text match → cross-region discovery → cached vision match). Best-effort: an
/// unidentified item simply continues with its as-extracted specs.
/// </summary>
[Activity("Daleel", "Item", "Identify product: text/vision match against the brand-model database")]
public sealed class IdentifyProductActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
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

            services.Log($"Identified {state.Model.Name} as “{id.CanonicalModelName}” ({id.Method}, {id.Confidence:P0}).");
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
/// New step — persist each source's raw specs (store listing, identified brand catalogue, scraped detail)
/// and the store product image to R2 under <c>site-data/{brand}/{model}/</c>, and stage the structured
/// sources for the merge. The DB only ever stores R2 URLs, never the raw blobs.
/// </summary>
[Activity("Daleel", "Item", "Save raw specs: persist each source's specs + image to R2 (site-data/)")]
public sealed class SaveRawSpecsActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
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

        var prefix = $"site-data/{brandKey}/{modelKey}";

        // 1. Store-listing structured specs (the as-extracted specs, NOT the raw markdown dump).
        var store = new Dictionary<string, string>(state.Model.Specs);
        store.Remove("details");
        if (store.Count > 0)
        {
            state.RawSpecsBySource.Add(SpecSource.Store(store));
            await ItemSpecPipeline.SaveJson(r2, state, $"{prefix}/store-listing.json", store, ct);
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
                await ItemSpecPipeline.SaveJson(r2, state, $"{prefix}/brand-site.json", brandSpecs, ct);
            }
        }

        // 3. The raw scraped detail markdown — archived for the record, never merged as a key-value spec.
        if (!string.IsNullOrWhiteSpace(state.Details))
        {
            var blob = JsonSerializer.Serialize(new { source = state.SourceUrl, content = state.Details });
            await ItemSpecPipeline.SaveJsonBlob(r2, state, $"{prefix}/scraped-detail.json", blob, ct);
        }

        // 4. The store product image → R2 (no hot-linking; the DB keeps the hosted URL only).
        var hosted = await ItemSpecPipeline.SafeStoreImage(r2, state.Model.ImageUrl, prefix, ct);
        if (!string.IsNullOrWhiteSpace(hosted) && hosted != state.Model.ImageUrl)
        {
            state.Result = state.Result with { ImageUrl = hosted };
        }

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
public sealed class MergeAndCleanSpecsActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
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

        services.Log($"Merged {state.RawSpecsBySource.Count} source(s) into {merged.Count} canonical spec(s) for {state.Model.Name}.");
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
public sealed class SaveFinalSpecsActivity : CodeActivity
{
    /// <summary>The BrandModel.FinalSpecsJson column cap (mirrors the entity configuration).</summary>
    private const int MaxFinalSpecsChars = 8000;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
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
        state.FinalSpecsR2Url = await ItemSpecPipeline.SafeStoreJson(r2, $"final-specs/{brandKey}/{modelKey}.json", json, ct);

        // Persist the canonical sheet onto the identified brand model so the model DB carries it directly.
        if (state.IdentifiedBrandModelId is { } id && json.Length <= MaxFinalSpecsChars)
        {
            var models = context.GetRequiredService<IBrandModelRepository>();
            try { await models.SaveFinalSpecsAsync(id, json, state.FinalSpecsR2Url, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort: the R2 copy + in-memory result remain authoritative */ }
        }

        services.Log($"Saved canonical spec sheet for {state.Model.Name} ({merged.Count} spec(s)).");
        state.RecordEvent(EventCategory.Extract, "item.finalspecs", state.FinalSpecsR2Url is null ? "db" : "r2",
            metadata: new Dictionary<string, object?>
            {
                ["item"] = state.Model.Name,
                ["specs"] = merged.Count,
                ["brandModelId"] = state.IdentifiedBrandModelId
            });
    }
}

/// <summary>Shared best-effort helpers for the spec-pipeline activities (R2 writes never fail the search).</summary>
internal static class ItemSpecPipeline
{
    /// <summary>Serializes a spec dictionary, uploads it to R2, and records the URL on the state.</summary>
    public static async Task SaveJson(
        IR2StorageService r2, ItemDeepDiveState state, string objectKey,
        IReadOnlyDictionary<string, string> specs, CancellationToken ct) =>
        await SaveJsonBlob(r2, state, objectKey, JsonSerializer.Serialize(specs), ct);

    /// <summary>Uploads a pre-serialized blob to R2 and records the URL on the state.</summary>
    public static async Task SaveJsonBlob(
        IR2StorageService r2, ItemDeepDiveState state, string objectKey, string json, CancellationToken ct)
    {
        var url = await SafeStoreJson(r2, objectKey, json, ct);
        if (url is not null)
        {
            state.RawSpecsR2Urls.Add(url);
        }
    }

    public static async Task<string?> SafeStoreJson(IR2StorageService r2, string objectKey, string json, CancellationToken ct)
    {
        try { return await r2.StoreJsonAsync(json, objectKey, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static async Task<string?> SafeStoreImage(IR2StorageService r2, string? url, string prefix, CancellationToken ct)
    {
        try { return await r2.StoreImageAsync(url, prefix, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return url; }
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
