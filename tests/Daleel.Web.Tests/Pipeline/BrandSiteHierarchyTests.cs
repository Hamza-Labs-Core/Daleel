using Daleel.Agent;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Services;
using Daleel.Web.Pipeline;
using Daleel.Web.Pipeline.Enrichment;
using Daleel.Web.Profiles;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The brand SITE HIERARCHY against real Postgres: (i) the (BrandId, Level, CountryCode) upsert
// discovery relies on updates in place — the unique index (NULLS NOT DISTINCT) forbids duplicates
// even for the country-less global row; (ii) the local-first fill — a market's LOCAL catalogue row
// owns the price while the global row still fills image/spec gaps; (iii) the refill's
// BrandRegionalUrl preference order local → regional → global → Brand.Website.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class BrandSiteHierarchyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    // ── (i) Discovery's upsert: one row per (brand, level, market), updated in place ────────────

    [Fact]
    public async Task Site_upserts_update_in_place_and_the_unique_index_forbids_duplicates()
    {
        using var ctx = new PostgresTestContext();
        var brand = await new BrandRepository(ctx.Db).UpsertAsync(
            new Brand { Name = "Acme", LastRefreshed = Now });
        var repo = new BrandSiteRepository(ctx.Db);

        // First discovery: all three levels.
        await repo.UpsertAsync(Site(brand.Id, BrandSiteLevel.Global, null, "https://acme.com", Now));
        await repo.UpsertAsync(Site(brand.Id, BrandSiteLevel.Regional, "mea", "https://acme-mea.com", Now));
        await repo.UpsertAsync(Site(brand.Id, BrandSiteLevel.Local, "jo", "https://acme.jo", Now));

        // Second discovery (a retry / a later search): same keys, newer facts — must UPDATE.
        var later = Now.AddDays(8);
        await repo.UpsertAsync(Site(brand.Id, BrandSiteLevel.Global, null, "https://www.acme.com", later));
        await repo.UpsertAsync(Site(brand.Id, BrandSiteLevel.Regional, "mea", "https://acme-mea.com/shop", later));
        await repo.UpsertAsync(Site(brand.Id, BrandSiteLevel.Local, "jo", "https://acme.jo/store", later));

        var rows = await new BrandSiteRepository(ctx.NewContext()).GetForBrandAsync(brand.Id);
        rows.Should().HaveCount(3, "the second run updates each level in place, never duplicates");
        rows.Single(s => s.Level == BrandSiteLevel.Global).Url.Should().Be("https://www.acme.com");
        rows.Single(s => s.Level == BrandSiteLevel.Regional).Url.Should().Be("https://acme-mea.com/shop");
        rows.Single(s => s.Level == BrandSiteLevel.Local).Url.Should().Be("https://acme.jo/store");
        rows.Should().OnlyContain(s => s.LastRefreshed == later);

        // A second local market coexists (its own key)…
        await new BrandSiteRepository(ctx.NewContext()).UpsertAsync(
            Site(brand.Id, BrandSiteLevel.Local, "ae", "https://acme.ae", later));
        (await new BrandSiteRepository(ctx.NewContext()).GetForBrandAsync(brand.Id)).Should().HaveCount(4);

        // …but a raw duplicate GLOBAL insert (CountryCode NULL twice) hits the unique index: the
        // NULLS-NOT-DISTINCT index is the backstop under concurrent writers, exactly like the
        // Brand/BrandModel races the repositories recover from.
        using var racer = ctx.NewContext();
        racer.BrandSites.Add(Site(brand.Id, BrandSiteLevel.Global, null, "https://dup.acme.com", later));
        var act = async () => await racer.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private static BrandSite Site(int brandId, string level, string? cc, string url, DateTimeOffset at) =>
        new() { BrandId = brandId, Level = level, CountryCode = cc, Url = url, LastRefreshed = at };

    // ── (ii) Local-first price fill ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Local_row_owns_the_price_while_the_global_row_fills_image_and_specs()
    {
        using var ctx = new PostgresTestContext();
        var brand = await new BrandRepository(ctx.Db).UpsertAsync(new Brand { Name = "Acme", LastRefreshed = Now });
        var models = new BrandModelRepository(ctx.Db);

        // The same product harvested at two levels: the Jordan storefront lists the in-market JOD
        // price (no image/specs); the global site lists a USD reference price WITH image + specs.
        await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Barista B9",
            SiteLevel = BrandSiteLevel.Local, SiteCountry = "jo",
            LocalPrice = 199m, Currency = "JOD",
            IsAvailable = true, SourceUrl = "https://acme.jo/b9", DiscoveredAt = Now, LastRefreshed = Now
        });
        await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Acme Barista B9",
            SiteLevel = BrandSiteLevel.Global, SiteCountry = null,
            LocalPrice = 299m, Currency = "USD",
            ImageUrl = "https://acme.com/img/b9.jpg", SpecsJson = """{"pressure":"9 bar"}""",
            IsAvailable = true, SourceUrl = "https://acme.com/b9", DiscoveredAt = Now, LastRefreshed = Now
        });

        var svc = BuildEnrichment(ctx);
        var item = new ProductModel
        {
            Name = "Acme Barista B9 espresso machine", Brand = "Acme",
            Offers = Array.Empty<PriceOffer>() // priceless + imageless + spec-less
        };

        // Jordan search: the LOCAL row wins the price even though the global row matches "better"
        // (more shared tokens) — global still fills the image and spec gaps.
        var filled = await svc.FillFromBrandDatabaseUnitAsync(new List<ProductModel> { item }, "jordan", default);

        filled.Should().NotBeNull();
        var jo = filled!.Single();
        jo.Offers.Should().ContainSingle("only the local storefront price may become an offer here");
        jo.Offers.Single().Price.Should().Be(199m);
        jo.Offers.Single().Currency.Should().Be("JOD");
        jo.ImageUrl.Should().Be("https://acme.com/img/b9.jpg", "image is market-agnostic — the global row fills the gap");
        jo.Specs.Should().ContainKey("pressure").WhoseValue.Should().Be("9 bar");

        // USA search over the same rows: the jo-local row is out of scope, so the global USD price
        // fills instead — the pair (level match + currency gate) keys prices to their market.
        var us = (await svc.FillFromBrandDatabaseUnitAsync(
            new List<ProductModel> { item }, "usa", default))!.Single();
        us.Offers.Should().ContainSingle().Which.Price.Should().Be(299m);
        us.Offers.Single().Currency.Should().Be("USD");
    }

    private static ItemEnrichmentService BuildEnrichment(PostgresTestContext ctx) =>
        new(
            new ProductProfileRepository(ctx.Db),
            new ProfileOptions { Now = () => Now, Ttl = TimeSpan.FromDays(30) },
            new StubAgentFactory(),
            new ScrapedPriceRepository(ctx.Db),
            new StubBrandCatalog(),
            new BrandRepository(ctx.Db),
            new BrandModelRepository(ctx.Db),
            new NoneIdentifier(),
            NullLogger<ItemEnrichmentService>.Instance);

    // ── (iii) Refill preference: local → regional → global → Brand.Website ─────────────────────

    [Fact]
    public async Task Refill_prefers_local_then_regional_then_global_for_the_models_brand_link()
    {
        using var ctx = new PostgresTestContext();
        // A FRESH brand row (site + description known): the handler takes the freshness-gate path,
        // so each run below exercises exactly the refill — no research, no paid calls (the ctx
        // agent factory throws).
        var brand = await new BrandRepository(ctx.Db).UpsertAsync(new Brand
        {
            Name = "Acme", Website = "https://website.acme.example",
            Description = "Espresso machines.", LastRefreshed = DateTimeOffset.UtcNow
        });
        var sites = new BrandSiteRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;
        await sites.UpsertAsync(Site(brand.Id, BrandSiteLevel.Global, null, "https://global.acme.example", now));
        await sites.UpsertAsync(Site(brand.Id, BrandSiteLevel.Regional, "mea", "https://regional.acme.example", now));
        await sites.UpsertAsync(Site(brand.Id, BrandSiteLevel.Local, "jo", "https://local.acme.example", now));

        (await RunRefillAsync(ctx)).Should().Be("https://local.acme.example",
            "the search's market (jo) has a local storefront — it wins");

        await DeleteSiteAsync(ctx, BrandSiteLevel.Local);
        (await RunRefillAsync(ctx)).Should().Be("https://regional.acme.example",
            "without a local site the regional variant is the next-best market fit");

        await DeleteSiteAsync(ctx, BrandSiteLevel.Regional);
        (await RunRefillAsync(ctx)).Should().Be("https://global.acme.example",
            "the recorded global site outranks the raw Website mirror");

        await DeleteSiteAsync(ctx, BrandSiteLevel.Global);
        (await RunRefillAsync(ctx)).Should().Be("https://website.acme.example",
            "with no hierarchy at all, the pre-hierarchy behaviour (Brand.Website) remains");
    }

    /// <summary>Runs the REAL handler over a fresh result and returns the patched BrandRegionalUrl.</summary>
    private static async Task<string?> RunRefillAsync(PostgresTestContext ctx)
    {
        var store = new FixedResultStore
        {
            Answer = new AgentAnswer
            {
                Products = new ProductSearchResult
                {
                    Query = "espresso machines", Geo = "jordan",
                    Models = new List<ProductModel> { new() { Name = "Acme Barista B9", Brand = "Acme" } },
                    Brands = new List<BrandInfo> { new() { Name = "Acme" } }
                }
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IBrandRepository>(new BrandRepository(ctx.Db));
        services.AddSingleton<IBrandSiteRepository>(new BrandSiteRepository(ctx.Db));
        services.AddSingleton<IItemEnrichmentService>(BuildEnrichment(ctx));

        var unitCtx = new EnrichmentUnitContext
        {
            Services = services.BuildServiceProvider(),
            Job = new SearchJob { Id = 7, UserId = "u1", Query = "espresso machines", Geo = "jordan" },
            Agent = () => throw new InvalidOperationException("the fresh path must never spend a paid call"),
            Results = store,
            Queue = new RecordingQueue()
        };

        var item = new EnrichmentWorkItem
        {
            Id = 1, SearchJobId = 7, UserId = "u1", HistoryEntryId = 3, ResultType = "products",
            Kind = EnrichmentUnit.BrandResearch,
            Payload = EnrichmentWorkQueue.Payload(new BrandPayload("Acme")),
            Attempts = 1, MaxAttempts = 4
        };

        var outcome = await new BrandResearchHandler(NullLogger<BrandResearchHandler>.Instance)
            .ExecuteAsync(item, unitCtx, default);
        outcome.Should().BeOfType<UnitOutcome.Done>();
        return store.Answer!.Products!.Models[0].BrandRegionalUrl;
    }

    private static async Task DeleteSiteAsync(PostgresTestContext ctx, string level)
    {
        using var db = ctx.NewContext();
        db.BrandSites.Remove(await db.BrandSites.SingleAsync(s => s.Level == level));
        await db.SaveChangesAsync();
    }

    // ── Minimal fakes (same shapes as BrandResearchTests / ItemEnrichmentServiceTests) ──────────

    private sealed class FixedResultStore : IEnrichedResultStore
    {
        public AgentAnswer? Answer { get; set; }

        public Task<AgentAnswer?> LoadAsync(int jobId, CancellationToken ct = default) => Task.FromResult(Answer);

        public Task<bool> PatchAsync(
            EnrichmentWorkItem item, Func<AgentAnswer, AgentAnswer?> mutate, CancellationToken ct = default)
        {
            if (Answer is null || mutate(Answer) is not { } patched)
            {
                return Task.FromResult(false);
            }

            Answer = patched;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingQueue : IEnrichmentWorkQueue
    {
        public Task EnqueueAsync(IReadOnlyList<EnrichmentWorkItem> items, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<bool> EnqueueFanOutAsync(
            int searchJobId, string selfKind, IReadOnlyList<EnrichmentWorkItem> children, CancellationToken ct = default) =>
            Task.FromResult(children.Count > 0);
        public Task<IReadOnlyList<EnrichmentWorkItem>> ClaimAsync(int max, TimeSpan lease, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EnrichmentWorkItem>>(Array.Empty<EnrichmentWorkItem>());
        public Task CompleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RetryAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequeueAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(long id, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> OpenCountAsync(int searchJobId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ReapExhaustedAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    /// <summary>Vision identification is out of scope here — always "couldn't identify".</summary>
    private sealed class NoneIdentifier : Daleel.Web.Identification.IProductIdentifier
    {
        public Task<Daleel.Web.Identification.ProductIdentification> IdentifyAsync(
            ProductModel item, CancellationToken ct = default) =>
            Task.FromResult(Daleel.Web.Identification.ProductIdentification.None);
    }

    // Resolve returns null so the Context.dev catalogue/harvest phases no-op without a key.
    private sealed class StubAgentFactory : IAgentFactory
    {
        public bool HasLlm() => false;
        public ProviderStatus Describe() => throw new NotSupportedException();
        public AgentService Build(AgentRequest request) => throw new NotSupportedException();
        public ILlmClient? TryBuildLlm(string? model = null) => null;
        public string? Resolve(string name) => null;
    }

    private sealed class StubBrandCatalog : IBrandCatalogService
    {
        public Task<int> HarvestAsync(string brandName, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> HarvestAsync(string brandName, string siteUrl, string level, string? countryCode, CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}
