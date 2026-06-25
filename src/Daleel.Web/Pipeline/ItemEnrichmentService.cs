using Daleel.Agent;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Profiles;

namespace Daleel.Web.Pipeline;

/// <summary>Outcome of an enrichment pass: the updated products (null when nothing changed) + the events to record.</summary>
public sealed record ItemEnrichmentResult(ProductSearchResult? Products, IReadOnlyList<PipelineEvent> Events);

/// <summary>
/// The per-item deep-dive, run AFTER the base results are shown so it never blocks first paint. For
/// each product it compares store prices and fills in specs — DB-first (a fresh saved
/// <see cref="ProductProfile"/> is reused with no network), and on a miss it scrapes the brand's
/// official page for the model (that's where Context.dev earns its keep: authoritative, region-correct
/// specs) and saves the result for next time.
/// </summary>
public interface IItemEnrichmentService
{
    Task<ItemEnrichmentResult> EnrichAsync(
        AgentService agent, ProductSearchResult products, Action<string> progress,
        string? searchId, CancellationToken ct);
}

public sealed class ItemEnrichmentService : IItemEnrichmentService
{
    /// <summary>How many items get a price-comparison + reuse pass (DB reads only).</summary>
    private const int MaxItems = 20;

    /// <summary>How many NEW (uncached) thin items get an actual network scrape per run.</summary>
    private const int MaxNewScrapes = 8;

    /// <summary>An item is "thin" (worth scraping) when it has fewer than this many specs.</summary>
    private const int ThinSpecThreshold = 3;

    /// <summary>Cap on a saved detail blob (entity column is 8000).</summary>
    private const int MaxDetailChars = 4000;

    private readonly IProductProfileRepository _repo;
    private readonly ProfileOptions _options;

    public ItemEnrichmentService(IProductProfileRepository repo, ProfileOptions options)
    {
        _repo = repo;
        _options = options;
    }

    public async Task<ItemEnrichmentResult> EnrichAsync(
        AgentService agent, ProductSearchResult products, Action<string> progress,
        string? searchId, CancellationToken ct)
    {
        var events = new List<PipelineEvent>();
        void Record(string category, string type, string provider,
            IReadOnlyDictionary<string, object?>? meta = null) =>
            events.Add(PipelineEventFactory.Custom(category, type, provider, searchId, metadata: meta));

        if (products.Models.Count == 0)
        {
            return new ItemEnrichmentResult(null, events);
        }

        var now = _options.Now();
        var ttl = _options.Ttl;
        var models = products.Models.Take(MaxItems).ToList();
        var enriched = new Dictionary<int, string>();

        // Phase 1 — price comparison + DB-first reuse (sequential: scoped DbContext isn't concurrency-safe).
        var toScrape = new List<(ProductModel m, int idx, string url, string key)>();
        for (var idx = 0; idx < models.Count; idx++)
        {
            var m = models[idx];
            var stores = m.Offers.Select(o => o.Source)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            progress($"Getting details on {m.Name} — comparing {stores} store price(s)…");
            Record(EventCategory.Extract, "item.compare", "pipeline",
                new Dictionary<string, object?> { ["item"] = m.Name, ["stores"] = stores, ["offers"] = m.Offers.Count });

            var key = ProductProfile.KeyFor(m.Brand, m.Model, m.Name);
            if (key.Length == 0)
            {
                continue;
            }

            var saved = await SafeGet(key, ct);
            if (saved is { } s && !s.IsStale(now, ttl) && !string.IsNullOrWhiteSpace(s.Details))
            {
                enriched[idx] = s.Details!;
                Record(EventCategory.Profile, "item.reuse", "profile",
                    new Dictionary<string, object?> { ["item"] = m.Name, ["cached"] = true });
                continue;
            }

            var url = OfficialOrCheapestUrl(m);
            if (url is not null && m.Specs.Count < ThinSpecThreshold && toScrape.Count < MaxNewScrapes)
            {
                toScrape.Add((m, idx, url, key));
            }
        }

        // Phase 2 — scrape the misses concurrently (network only, no DB).
        var scraped = toScrape.Count == 0
            ? Array.Empty<(int idx, string url, string key, ProductModel m, string? content)>()
            : await Task.WhenAll(toScrape.Select(async t =>
            {
                progress($"Fetching official specs for {t.m.Name}…");
                var page = await agent.ReadPageAsync(t.url, ct);
                var content = page is null
                    ? null
                    : page.Content.Length <= MaxDetailChars ? page.Content : page.Content[..MaxDetailChars];
                return (t.idx, t.url, t.key, t.m, content);
            }));

        // Phase 3 — persist each fresh deep-dive (sequential writes) so it's reused next time.
        var fresh = 0;
        foreach (var r in scraped)
        {
            if (string.IsNullOrWhiteSpace(r.content))
            {
                continue;
            }

            enriched[r.idx] = r.content!;
            fresh++;
            Record(EventCategory.Extract, "item.deepdive", "context.dev",
                new Dictionary<string, object?> { ["item"] = r.m.Name, ["url"] = r.url });
            await SafeUpsert(new ProductProfile
            {
                Name = r.m.Name, Brand = r.m.Brand, Model = r.m.Model, NameKey = r.key,
                Details = r.content, SourceUrl = r.url, LastRefreshed = now
            }, ct);
        }

        if (enriched.Count == 0)
        {
            return new ItemEnrichmentResult(null, events);
        }

        var rebuilt = models
            .Select((m, idx) =>
            {
                if (!enriched.TryGetValue(idx, out var detail))
                {
                    return m;
                }
                var specs = new Dictionary<string, string>(m.Specs) { ["details"] = detail };
                return m with { Specs = specs };
            })
            .Concat(products.Models.Skip(MaxItems))
            .ToList();

        progress($"Deep-dived {enriched.Count} item(s) — {fresh} new, {enriched.Count - fresh} reused from earlier searches.");
        return new ItemEnrichmentResult(products with { Models = rebuilt }, events);
    }

    /// <summary>
    /// The page to scrape for specs: prefer the brand's OWN product page (an offer classified as a
    /// brand page — authoritative, region-correct specs), then the cheapest offer, then any offer.
    /// </summary>
    public static string? OfficialOrCheapestUrl(ProductModel m) =>
        m.Offers.FirstOrDefault(o => o.SourceType == ResultType.BrandPage && !string.IsNullOrWhiteSpace(o.Url))?.Url
        ?? m.Offers.FirstOrDefault(o => o.IsLowest && !string.IsNullOrWhiteSpace(o.Url))?.Url
        ?? m.Offers.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.Url))?.Url;

    private async Task<ProductProfile?> SafeGet(string key, CancellationToken ct)
    {
        try { return await _repo.GetByKeyAsync(key, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task SafeUpsert(ProductProfile profile, CancellationToken ct)
    {
        try { await _repo.UpsertAsync(profile, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort: saving a deep-dive must never fail the search */ }
    }
}
