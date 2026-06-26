using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// The persisted product deep-dive: a model is scraped once and reused by every later search that
/// surfaces it. Covers the normalized brand+model key (so the same product from two different
/// searches maps to one row), the insert-then-update upsert path, and the staleness check that
/// decides reuse vs. re-scrape.
/// </summary>
public class ProductProfileRepositoryTests
{
    [Fact]
    public void KeyFor_SameProductDifferentCasingAndPunctuation_MapsToOneKey()
    {
        var a = ProductProfile.KeyFor("Samsung", "AR24", "Samsung AR24 Wind-Free");
        var b = ProductProfile.KeyFor("  samsung ", "ar24!", "totally different display name");

        a.Should().Be("samsung ar24");
        b.Should().Be(a, "the key is brand+model normalized, independent of the display name");
    }

    [Fact]
    public void KeyFor_FallsBackToName_WhenNoBrandOrModel()
    {
        ProductProfile.KeyFor(null, null, "LG DualCool 1.5HP").Should().Be("lg dualcool 15hp");
        ProductProfile.KeyFor(null, null, "").Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_RoundTripsAndReusesByKey_WithoutDuplicating()
    {
        using var ctx = new SqliteTestContext();
        var repo = new ProductProfileRepository(ctx.Db);

        await repo.UpsertAsync(new ProductProfile
        {
            Name = "Samsung AR24 Wind-Free",
            Brand = "Samsung",
            Model = "AR24",
            NameKey = ProductProfile.KeyFor("Samsung", "AR24", "Samsung AR24 Wind-Free"),
            Details = "Cooling: 24000 BTU; Energy: A++",
            SourceUrl = "https://store.jo/ar24",
            LastRefreshed = DateTimeOffset.UtcNow
        });

        var fetched = await repo.GetByKeyAsync(ProductProfile.KeyFor("samsung", "ar24", "x"));
        fetched.Should().NotBeNull();
        fetched!.Details.Should().Contain("24000 BTU");

        // A later search re-dives the same model: updates in place, not a duplicate row.
        await repo.UpsertAsync(new ProductProfile
        {
            Name = "Samsung AR24",
            Brand = "Samsung",
            Model = "AR24",
            NameKey = ProductProfile.KeyFor("Samsung", "AR24", "Samsung AR24"),
            Details = "Cooling: 24000 BTU; Energy: A+++ (updated)",
            LastRefreshed = DateTimeOffset.UtcNow
        });

        (await repo.CountAsync()).Should().Be(1);
        (await repo.GetByKeyAsync("samsung ar24"))!.Details.Should().Contain("A+++");
    }

    [Fact]
    public void IsStale_UsesRefreshAgeAgainstTtl()
    {
        var now = DateTimeOffset.UtcNow;
        var p = new ProductProfile { Name = "X", LastRefreshed = now.AddDays(-31) };

        p.IsStale(now, TimeSpan.FromDays(30)).Should().BeTrue();
        p.IsStale(now, TimeSpan.FromDays(60)).Should().BeFalse();
    }
}
