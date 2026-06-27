using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// Exercises the smart-identification persistence: the vision-match cache (insert/overwrite, the unique
/// pair key) and the new <see cref="BrandModel"/> fields (list union, first-seen <c>DiscoveredAt</c>, and
/// the forward-only final-specs write).
/// </summary>
public class VisionAndBrandModelSmartTests
{
    private static async Task<BrandModel> SeedModelAsync(PostgresTestContext ctx, string name = "Galaxy S24")
    {
        var brand = await new BrandRepository(ctx.Db).UpsertAsync(
            new Brand { Name = "Samsung", LastRefreshed = DateTimeOffset.UtcNow });
        return await new BrandModelRepository(ctx.Db).UpsertAsync(new BrandModel
        {
            BrandId = brand.Id,
            ModelName = name,
            LastRefreshed = DateTimeOffset.UtcNow
        });
    }

    [Fact]
    public async Task VisionCache_Upsert_InsertsThenOverwritesOnSamePair()
    {
        using var ctx = new PostgresTestContext();
        var model = await SeedModelAsync(ctx);
        var repo = new VisionMatchCacheRepository(ctx.Db);

        await repo.UpsertAsync(new VisionMatchCache
        {
            StoreImageHash = "hash1", BrandModelId = model.Id, Confidence = 0.4, MatchedAt = DateTimeOffset.UtcNow
        });
        await repo.UpsertAsync(new VisionMatchCache
        {
            StoreImageHash = "hash1", BrandModelId = model.Id, Confidence = 0.91,
            MatchedModelName = "Galaxy S24", MatchedAt = DateTimeOffset.UtcNow
        });

        (await repo.CountAsync()).Should().Be(1, "the (hash, model) pair is unique — the second write updates");
        var cached = await repo.GetAsync("hash1", model.Id);
        cached!.Confidence.Should().Be(0.91);
        cached.IsMatch.Should().BeTrue();
        cached.MatchedModelName.Should().Be("Galaxy S24");
    }

    [Fact]
    public async Task VisionCache_Get_ReturnsNull_WhenNeverCompared()
    {
        using var ctx = new PostgresTestContext();
        var model = await SeedModelAsync(ctx);
        (await new VisionMatchCacheRepository(ctx.Db).GetAsync("never", model.Id)).Should().BeNull();
    }

    [Fact]
    public async Task BrandModel_Upsert_UnionsListsAndKeepsFirstDiscoveredAt()
    {
        using var ctx = new PostgresTestContext();
        var brand = await new BrandRepository(ctx.Db).UpsertAsync(
            new Brand { Name = "Samsung", LastRefreshed = DateTimeOffset.UtcNow });
        var repo = new BrandModelRepository(ctx.Db);

        var firstSeen = DateTimeOffset.UtcNow.AddDays(-10);
        await repo.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24",
            ImageR2Urls = new List<string> { "https://img/1.jpg" },
            RegionalAliases = new List<string> { "SM-S921B/JO" },
            DiscoveredAt = firstSeen, LastRefreshed = firstSeen
        });
        await repo.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24",
            ImageR2Urls = new List<string> { "https://img/2.jpg", "https://img/1.jpg" },
            RegionalAliases = new List<string> { "SM-S921B/AE" },
            DiscoveredAt = DateTimeOffset.UtcNow, LastRefreshed = DateTimeOffset.UtcNow
        });

        var saved = (await repo.ListByBrandAsync(brand.Id)).Single();
        saved.ImageR2Urls.Should().BeEquivalentTo("https://img/1.jpg", "https://img/2.jpg");
        saved.RegionalAliases.Should().BeEquivalentTo("SM-S921B/JO", "SM-S921B/AE");
        saved.DiscoveredAt.Should().BeCloseTo(firstSeen, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SaveFinalSpecs_WritesThenCoalescesNulls()
    {
        using var ctx = new PostgresTestContext();
        var model = await SeedModelAsync(ctx);
        var repo = new BrandModelRepository(ctx.Db);

        await repo.SaveFinalSpecsAsync(model.Id, """{"ram":"12 GB"}""", "https://r2/final-specs/x.json");
        var afterWrite = await repo.GetByIdAsync(model.Id);
        afterWrite!.FinalSpecsJson.Should().Be("""{"ram":"12 GB"}""");
        afterWrite.FinalSpecsR2Url.Should().Be("https://r2/final-specs/x.json");

        // A later call with nulls must not blank a good sheet.
        await repo.SaveFinalSpecsAsync(model.Id, null, null);
        var afterNull = await repo.GetByIdAsync(model.Id);
        afterNull!.FinalSpecsJson.Should().Be("""{"ram":"12 GB"}""");
    }

    [Fact]
    public async Task SaveFinalSpecs_OnMissingModel_IsNoOp()
    {
        using var ctx = new PostgresTestContext();
        await new BrandModelRepository(ctx.Db).SaveFinalSpecsAsync(9999, "{}", null); // must not throw
    }
}
