using Daleel.Search.Providers;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Profiles;
using Daleel.Web.Services;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace Daleel.Web.Pipeline.SubWorkflows;

// The five steps of the per-store research sub-workflow. Steps 1–4 reuse the IStoreRepository +
// IProfileResearcher pieces (the researcher scrapes the site, verifies on Google Maps and extracts
// contact info in one Context.dev + Places pass); each step folds the slice it owns onto the result.
// Step 5 harvests live catalogue prices — the one genuinely new per-store network call.

/// <summary>Step 1 — scrape the store's site for listings/contact, DB-first.</summary>
[Activity("Daleel", "Store", "Scrape the store site: serve the saved profile when fresh, else research")]
public sealed class ScrapeStoreSiteActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        var repo = context.GetRequiredService<IStoreRepository>();
        var options = context.GetRequiredService<ProfileOptions>();

        state.Log($"Looking up {state.Store.Name}…");
        state.Existing = await SafeGet(repo, state.Store.Name, context.CancellationToken);
        if (state.Existing is not null && !state.Existing.IsStale(options.Now(), options.Ttl))
        {
            state.ResolvedFromCache = true;
            state.Saved = state.Existing;
            return;
        }

        var researcher = context.GetRequiredService<IProfileResearcher>();
        if (!researcher.IsAvailable)
        {
            state.Saved = state.Existing; // degrade to the (possibly stale) saved profile
            return;
        }

        state.Log($"Scraping {state.Store.Name}'s site via Context.dev…");
        state.Researched = await SafeResearch(researcher, state.Store.Name, state.Geo, context.CancellationToken);
        state.Saved = state.Researched ?? state.Existing;
        state.RecordEvent(EventCategory.Profile, "profile.store", "context.dev",
            success: state.Researched is not null,
            metadata: new Dictionary<string, object?>
            {
                ["name"] = state.Store.Name,
                ["found"] = state.Researched is not null
            });
    }

    private static async Task<Data.Store?> SafeGet(IStoreRepository repo, string name, CancellationToken ct)
    {
        try { return await repo.GetByNameAsync(name, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<Data.Store?> SafeResearch(
        IProfileResearcher researcher, string name, string geo, CancellationToken ct)
    {
        try { return await researcher.ResearchStoreAsync(name, geo, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}

/// <summary>Step 2 — cross-reference Google Maps for verified location, rating and review count.</summary>
[Activity("Daleel", "Store", "Verify on Google Maps: fold in verified rating, reviews and coordinates")]
public sealed class VerifyOnMapsActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        if (state.Saved is not { } saved)
        {
            return ValueTask.CompletedTask;
        }

        state.Log($"Verifying {state.Store.Name} on Google Maps…");
        var s = state.Result;
        state.Result = s with
        {
            // Prefer the live result's own fields; backfill from the Maps-verified profile.
            Rating = s.Rating ?? saved.GoogleRating ?? saved.Rating,
            ReviewCount = s.ReviewCount ?? saved.GoogleReviewCount,
            Latitude = s.Latitude ?? saved.Latitude,
            Longitude = s.Longitude ?? saved.Longitude
        };
        state.RecordEvent(EventCategory.Places, "store.verify", "places",
            success: saved.IsVerified,
            metadata: new Dictionary<string, object?>
            {
                ["name"] = state.Store.Name,
                ["verified"] = saved.IsVerified
            });
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 3 — fold the store's contact details (phone, address) onto the result.</summary>
[Activity("Daleel", "Store", "Extract contact info: phone and address from the verified profile")]
public sealed class ExtractContactInfoActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        if (state.Saved is not { } saved)
        {
            return ValueTask.CompletedTask;
        }

        var s = state.Result;
        state.Result = s with
        {
            Address = s.Address ?? saved.Address ?? saved.Location,
            Phone = s.Phone ?? saved.Phone
        };
        return ValueTask.CompletedTask;
    }
}

/// <summary>Step 4 — persist the verified store profile and finalize its website link.</summary>
[Activity("Daleel", "Store", "Save the verified store profile to the database")]
public sealed class SaveStoreProfileActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        if (state.Saved is { } saved)
        {
            var s = state.Result;
            state.Result = s with
            {
                Url = s.Url ?? saved.Website ?? saved.GoogleMapsUrl,
                // Carry the real database id (from a cache hit) so the store page routes by it; the
                // freshly-researched path backfills it below once the profile is persisted.
                DbId = saved.Id > 0 ? saved.Id : s.DbId
            };
        }

        if (state.Researched is null)
        {
            return; // served from cache (or nothing found) — nothing new to persist
        }

        var repo = context.GetRequiredService<IStoreRepository>();
        var options = context.GetRequiredService<ProfileOptions>();
        state.Researched.LastRefreshed = options.Now();
        var persisted = await SafeUpsert(repo, state.Researched, context.CancellationToken);
        if (persisted is not null)
        {
            if (persisted.Id > 0)
            {
                state.Result = state.Result with { DbId = persisted.Id };
            }
            state.Log($"Saved {state.Store.Name}'s profile.");
        }
    }

    private static async Task<Data.Store?> SafeUpsert(IStoreRepository repo, Data.Store store, CancellationToken ct)
    {
        try { return await repo.UpsertAsync(store, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}

/// <summary>
/// Step 5 — harvest the store's product catalogue WITH PRICING from its website
/// (Context.dev <c>/v1/brand/ai/products</c>). Best-effort and bounded by the sub-workflow timeout: any
/// failure just means "no live prices for this store". Records how many priced products were found.
/// </summary>
[Activity("Daleel", "Store", "Scrape prices: harvest the store's catalogue prices via Context.dev")]
public sealed class ScrapePricesActivity : CodeActivity
{
    /// <summary>Catalogue products harvested per store — bounded so one store can't flood the run.</summary>
    private const int MaxProducts = 12;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var state = context.GetRequiredService<StoreResearchState>();
        var factory = context.GetRequiredService<IAgentFactory>();

        var key = factory.Resolve("CONTEXT_DEV_API_KEY");
        var domain = DomainOf(state.Result.Url);
        if (string.IsNullOrWhiteSpace(key) || domain is null)
        {
            return; // no Context.dev key or no resolvable store domain — nothing to crawl
        }

        state.Log($"Reading {state.Store.Name}'s catalogue for prices…");
        var provider = new ContextDevProvider(key);
        var products = await SafeCatalog(provider, domain, context.CancellationToken);
        state.PricedProducts = products.Count(p => p.Price is not null);
        state.RecordEvent(EventCategory.Extract, "store.prices", "context.dev",
            metadata: new Dictionary<string, object?>
            {
                ["store"] = state.Store.Name,
                ["domain"] = domain,
                ["products"] = products.Count,
                ["priced"] = state.PricedProducts
            });
        if (state.PricedProducts > 0)
        {
            state.Log($"Found {state.PricedProducts} priced product(s) at {state.Store.Name}.");
        }
    }

    private static async Task<IReadOnlyList<CatalogProduct>> SafeCatalog(
        ContextDevProvider provider, string domain, CancellationToken ct)
    {
        try
        {
            return await provider.ExtractProductsAsync(domain, maxProducts: MaxProducts, cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; } // genuine cancellation/timeout must propagate
        catch
        {
            return Array.Empty<CatalogProduct>(); // any other failure → no live prices for this store
        }
    }

    /// <summary>Bare registrable domain of a store URL (drops scheme + leading www), or null.</summary>
    private static string? DomainOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var s = url.Trim();
        if (!s.Contains("://"))
        {
            s = "https://" + s;
        }

        if (!Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            return null;
        }

        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
    }
}
