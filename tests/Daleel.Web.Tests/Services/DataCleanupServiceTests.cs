using Daleel.Web.Data;
using Daleel.Web.Services;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daleel.Web.Tests.Services;

/// <summary>
/// The periodic catalogue sweep behind <see cref="DataCleanupService"/>: it must drop rows too thin to be
/// useful (no price/source/name, no location/website, no associated product) and rows that were
/// misclassified during a search (an article saved as a product, a social page saved as a brand), while
/// leaving every well-formed row untouched. Re-running over the cleaned table must change nothing
/// (idempotent). Runs against real PostgreSQL so the EF queries match production.
/// </summary>
public class DataCleanupServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Sweep_RemovesThinAndMisclassifiedRows_KeepsValidOnes()
    {
        using var ctx = new PostgresTestContext();

        // A valid brand with a real, priced, sourced model — must survive in full.
        var samsung = new Brand
        {
            Name = "Samsung", NameKey = Brand.Normalize("Samsung"),
            PopularModels = new List<string> { "Galaxy S24" },
            Website = "https://samsung.com", LastRefreshed = Now
        };
        // A brand with NO associated product and no popular models — junk.
        var emptyBrand = new Brand { Name = "Ghost", NameKey = Brand.Normalize("Ghost"), LastRefreshed = Now };
        // A "brand" that's really a social page (misclassified store/brand) — junk.
        var redditBrand = new Brand
        {
            Name = "r/JordanShopping", NameKey = Brand.Normalize("r/JordanShopping"),
            PopularModels = new List<string> { "thread" }, Website = "https://reddit.com/r/JordanShopping",
            LastRefreshed = Now
        };
        ctx.Db.Brands.AddRange(samsung, emptyBrand, redditBrand);
        await ctx.Db.SaveChangesAsync();

        // Valid product: name + price + source url.
        ctx.Db.BrandModels.Add(new BrandModel
        {
            BrandId = samsung.Id, ModelName = "Galaxy S24 Ultra", ModelKey = BrandModel.Normalize("Galaxy S24 Ultra"),
            LocalPrice = 999, Currency = "JOD", SourceUrl = "https://samsung.com/jo/galaxy-s24-ultra",
            LastRefreshed = Now, DiscoveredAt = Now
        });
        // Kept: a model discovered from a source page but no price yet (the deep-dive fills the price later).
        ctx.Db.BrandModels.Add(new BrandModel
        {
            BrandId = samsung.Id, ModelName = "Mystery Phone", ModelKey = BrandModel.Normalize("Mystery Phone"),
            SourceUrl = "https://samsung.com/jo/mystery", LastRefreshed = Now, DiscoveredAt = Now
        });
        // Kept: priced but no stored source url — still a real, useful catalogue row.
        ctx.Db.BrandModels.Add(new BrandModel
        {
            BrandId = samsung.Id, ModelName = "No Source AC", ModelKey = BrandModel.Normalize("No Source AC"),
            LocalPrice = 320, Currency = "JOD", LastRefreshed = Now, DiscoveredAt = Now
        });
        // Thin product: neither a price nor a source url — no signal at all, junk.
        ctx.Db.BrandModels.Add(new BrandModel
        {
            BrandId = samsung.Id, ModelName = "Floating Rumor", ModelKey = BrandModel.Normalize("Floating Rumor"),
            LastRefreshed = Now, DiscoveredAt = Now
        });
        // Misclassified: an article saved as a product (priced+sourced, but the source is a buying guide).
        ctx.Db.BrandModels.Add(new BrandModel
        {
            BrandId = samsung.Id, ModelName = "Best ACs in Jordan 2026 - Buying Guide",
            ModelKey = BrandModel.Normalize("Best ACs in Jordan 2026 - Buying Guide"),
            LocalPrice = 1, Currency = "JOD", SourceUrl = "https://medium.com/best-acs",
            LastRefreshed = Now, DiscoveredAt = Now
        });

        // Valid store: name + location. Plus a thin store (name only) and a misclassified social "store".
        ctx.Db.Stores.AddRange(
            new Store { Name = "Smart Buy", NameKey = Store.Normalize("Smart Buy"), Location = "Amman, Jordan", LastRefreshed = Now },
            new Store { Name = "Nowhere Store", NameKey = Store.Normalize("Nowhere Store"), LastRefreshed = Now },
            new Store { Name = "AC Facebook Group", NameKey = Store.Normalize("AC Facebook Group"), Website = "https://facebook.com/groups/ac", LastRefreshed = Now });
        await ctx.Db.SaveChangesAsync();

        // ── Sweep on a fresh context (simulates the background scope) ──
        var report = await DataCleanupService.SweepAsync(ctx.NewContext());

        report.ProductsRemoved.Should().Be(2, "the no-signal row and the article-as-product row go; price-or-source rows stay");
        report.StoresRemoved.Should().Be(2, "the name-only and the social-page store go");
        report.BrandsRemoved.Should().Be(2, "the product-less brand and the social-page brand go");

        await using var verify = ctx.NewContext();
        (await verify.BrandModels.Select(m => m.ModelName).ToListAsync())
            .Should().BeEquivalentTo(new[] { "Galaxy S24 Ultra", "Mystery Phone", "No Source AC" });
        (await verify.Stores.Select(s => s.Name).ToListAsync())
            .Should().ContainSingle().Which.Should().Be("Smart Buy");
        (await verify.Brands.Select(b => b.Name).ToListAsync())
            .Should().ContainSingle().Which.Should().Be("Samsung");
    }

    [Fact]
    public async Task Sweep_IsIdempotent_SecondRunRemovesNothing()
    {
        using var ctx = new PostgresTestContext();

        var junk = new Brand { Name = "Junk", NameKey = Brand.Normalize("Junk"), LastRefreshed = Now };
        ctx.Db.Brands.Add(junk);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.BrandModels.Add(new BrandModel
        {
            BrandId = junk.Id, ModelName = "Priceless Model", ModelKey = BrandModel.Normalize("Priceless Model"),
            LastRefreshed = Now, DiscoveredAt = Now // no price AND no source url → junk
        });
        await ctx.Db.SaveChangesAsync();

        var first = await DataCleanupService.SweepAsync(ctx.NewContext());
        first.TotalRemoved.Should().BeGreaterThan(0);

        var second = await DataCleanupService.SweepAsync(ctx.NewContext());
        second.TotalRemoved.Should().Be(0, "a second sweep over an already-clean table is a no-op");
    }

    [Fact]
    public async Task Sweep_RemovesBrandLeftProductlessAfterItsOnlyModelIsPurged()
    {
        using var ctx = new PostgresTestContext();

        // A brand whose ONLY model is junk (no price). After the product sweep the brand has zero
        // associated products and (no popular models) must itself be removed in the same pass.
        var brand = new Brand { Name = "OrphanCo", NameKey = Brand.Normalize("OrphanCo"), Website = "https://orphanco.com", LastRefreshed = Now };
        ctx.Db.Brands.Add(brand);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.BrandModels.Add(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Pricey Nothing", ModelKey = BrandModel.Normalize("Pricey Nothing"),
            LastRefreshed = Now, DiscoveredAt = Now // no price AND no source url → junk
        });
        await ctx.Db.SaveChangesAsync();

        var report = await DataCleanupService.SweepAsync(ctx.NewContext());

        report.ProductsRemoved.Should().Be(1);
        report.BrandsRemoved.Should().Be(1, "the brand is product-less once its only (junk) model is purged");

        await using var verify = ctx.NewContext();
        (await verify.Brands.CountAsync()).Should().Be(0);
    }
}
