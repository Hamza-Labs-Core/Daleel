using Daleel.Web.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// Two correctness properties the brand upserts must hold and previously had no coverage for:
/// (1) a partial re-harvest must never blank out previously-good fields with nulls (H-2), and
/// (2) two concurrent upserts for the same key must converge to a single merged row without throwing —
/// the <c>DbUpdateException</c> detach/reload/re-save recovery is the entire correctness argument for
/// parallel harvesting (H-4 / T-1).
/// </summary>
public class BrandUpsertResilienceTests
{
    // ── H-2: null-coalescing on re-harvest ────────────────────────────────────────

    [Fact]
    public async Task BrandModel_PartialReharvest_DoesNotBlankExistingFields()
    {
        using var ctx = new PostgresTestContext();
        var brand = await new BrandRepository(ctx.Db).UpsertAsync(
            new Brand { Name = "Samsung", LastRefreshed = DateTimeOffset.UtcNow });
        var repo = new BrandModelRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        // First harvest: full data.
        await repo.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24", Category = "Smartphone",
            SpecsJson = "{\"ram\":\"8GB\"}", LocalPrice = 900m, GlobalPrice = 950m, Currency = "JOD",
            SourceUrl = "https://samsung.com/jo/s24", IsAvailable = true, LastRefreshed = now
        });

        // Second harvest: flaky extraction returned only the name — every enrichment field is null.
        await repo.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24", Category = null, SpecsJson = null,
            LocalPrice = null, GlobalPrice = null, Currency = null, SourceUrl = null,
            IsAvailable = false, LastRefreshed = now.AddHours(1)
        });

        var saved = (await repo.ListByBrandAsync(brand.Id)).Single();
        saved.Category.Should().Be("Smartphone", "a null in the re-harvest must not erase a known category");
        saved.SpecsJson.Should().Be("{\"ram\":\"8GB\"}");
        saved.LocalPrice.Should().Be(900m);
        saved.GlobalPrice.Should().Be(950m);
        saved.Currency.Should().Be("JOD");
        saved.SourceUrl.Should().Be("https://samsung.com/jo/s24");
        saved.IsAvailable.Should().BeFalse("availability is a current-harvest fact and still updates");
        saved.LastRefreshed.Should().BeCloseTo(now.AddHours(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task BrandModel_Reharvest_OverwritesFieldsThatHaveNewValues()
    {
        using var ctx = new PostgresTestContext();
        var brand = await new BrandRepository(ctx.Db).UpsertAsync(
            new Brand { Name = "Samsung", LastRefreshed = DateTimeOffset.UtcNow });
        var repo = new BrandModelRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        await repo.UpsertAsync(new BrandModel { BrandId = brand.Id, ModelName = "S24", LocalPrice = 900m, LastRefreshed = now });
        await repo.UpsertAsync(new BrandModel { BrandId = brand.Id, ModelName = "S24", LocalPrice = 850m, LastRefreshed = now.AddHours(1) });

        (await repo.ListByBrandAsync(brand.Id)).Single().LocalPrice.Should().Be(850m, "a real new price still wins");
    }

    [Fact]
    public async Task Brand_PartialReResearch_KeepsListsAndOptionalFields()
    {
        using var ctx = new PostgresTestContext();
        var repo = new BrandRepository(ctx.Db);
        var now = DateTimeOffset.UtcNow;

        await repo.UpsertAsync(new Brand
        {
            Name = "Bosch", CountryOfOrigin = "Germany", ReputationScore = 8.5,
            Description = "Appliance maker", Pros = new() { "reliable", "quiet" }, Cons = new() { "pricey" },
            PopularModels = new() { "Series 6" }, PriceRange = "premium", Website = "https://bosch.com",
            LastRefreshed = now
        });

        // Re-research came back thin: empty lists, null optionals.
        await repo.UpsertAsync(new Brand
        {
            Name = "Bosch", CountryOfOrigin = null, ReputationScore = null, Description = null,
            Pros = new(), Cons = new(), PopularModels = new(), PriceRange = null, Website = null,
            LastRefreshed = now.AddHours(1)
        });

        var saved = await repo.GetByNameAsync("Bosch");
        saved!.CountryOfOrigin.Should().Be("Germany");
        saved.ReputationScore.Should().Be(8.5);
        saved.Description.Should().Be("Appliance maker");
        saved.Pros.Should().BeEquivalentTo("reliable", "quiet");
        saved.Cons.Should().BeEquivalentTo("pricey");
        saved.PopularModels.Should().BeEquivalentTo("Series 6");
        saved.Website.Should().Be("https://bosch.com");
    }

    // ── T-1: concurrent upsert recovery ───────────────────────────────────────────

    [Fact]
    public async Task BrandModel_ConcurrentUpsertsForSameKey_ConvergeToOneRow()
    {
        // A dedicated Postgres database so each context gets its OWN connection (its own change tracker),
        // the way two request scopes would — genuine contention on the (BrandId, ModelKey) unique index.
        var connStr = PostgresTestServer.CreateFreshDatabase();

        using (var seed = NewContext(connStr))
        {
            seed.Database.EnsureCreated();
            seed.Brands.Add(new Brand { Id = 1, Name = "Samsung", NameKey = "samsung", LastRefreshed = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var ctxA = NewContext(connStr);
        using var ctxB = NewContext(connStr);
        var now = DateTimeOffset.UtcNow;

        var upsertA = new BrandModelRepository(ctxA).UpsertAsync(new BrandModel
        { BrandId = 1, ModelName = "Galaxy S24", LocalPrice = 900m, IsAvailable = true, LastRefreshed = now });
        var upsertB = new BrandModelRepository(ctxB).UpsertAsync(new BrandModel
        { BrandId = 1, ModelName = "Galaxy S24", LocalPrice = 850m, IsAvailable = true, LastRefreshed = now });

        // Neither concurrent upsert may throw — the recovery absorbs the unique-index race.
        var act = async () => await Task.WhenAll(upsertA, upsertB);
        await act.Should().NotThrowAsync();

        // Exactly one row survives regardless of which writer won, holding one of the two prices.
        using var verify = NewContext(connStr);
        var rows = await verify.BrandModels.Where(m => m.BrandId == 1).ToListAsync();
        rows.Should().ContainSingle("the unique (BrandId, ModelKey) index plus the recovery collapse the race to one row");
        rows[0].LocalPrice.Should().BeOneOf(900m, 850m);
    }

    private static DaleelDbContext NewContext(string connStr) =>
        new(new DbContextOptionsBuilder<DaleelDbContext>().UseNpgsql(connStr).Options);
}
