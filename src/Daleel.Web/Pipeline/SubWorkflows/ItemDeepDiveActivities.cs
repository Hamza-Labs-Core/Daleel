using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Profiles;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The five steps of the per-item deep-dive sub-workflow. Specs are DB-first (a fresh saved
// ProductProfile is reused with no network); a thin item with an official/cheapest offer URL is scraped
// for authoritative, region-correct specs and saved for next time. Price comparison + review collection
// run over the offers already aggregated upstream — no extra network for those.

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

        state.Log($"Fetching official specs for {state.Model.Name}…");
        var page = await state.Agent.ReadPageAsync(url, ct);
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
        var offers = state.Result.Offers;
        var stores = offers
            .Select(o => o.Source)
            .Where(src => !string.IsNullOrWhiteSpace(src))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        state.Log($"Comparing {stores} store price(s) for {state.Model.Name}…");
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
        state.Log($"Saved deep-dive for {state.Model.Name}.");
    }

    private static async Task SafeUpsert(IProductProfileRepository repo, ProductProfile profile, CancellationToken ct)
    {
        try { await repo.UpsertAsync(profile, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort: saving a deep-dive must never fail the search */ }
    }
}
