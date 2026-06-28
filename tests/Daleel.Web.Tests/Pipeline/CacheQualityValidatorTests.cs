using Daleel.Agent;
using Daleel.Core.Models;
using Daleel.Web.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// The smart-cache completeness scorer. A cache hit is only as good as the data in it: these pin the
/// thresholds (serve ≥80, reject &lt;30, refill in between), the per-dimension scoring, and — crucially —
/// the partial-enrichment targeting (which products/brands/stores get re-scraped), since that's what
/// keeps a re-enrichment from re-scraping data that's already there.
/// </summary>
public class CacheQualityValidatorTests
{
    private static readonly ICacheQualityValidator Validator = new CacheQualityValidator();

    // ── Builders ─────────────────────────────────────────────────────────────────
    private static ProductModel CompleteProduct(string name = "Samsung AR24") => new()
    {
        Name = name,
        Model = "AR24TXHYCWK",
        ImageUrl = "https://img/ar24.jpg",
        Specs = new Dictionary<string, string> { ["BTU"] = "24000", ["Energy"] = "A++", ["SKU"] = "AR24-JO" },
        Offers = new[] { new PriceOffer { Source = "SmartBuy", Url = "https://s/x", Price = 520m } }
    };

    private static BrandInfo CompleteBrand(string name = "Samsung") => new()
    {
        Name = name,
        LogoUrl = "https://img/samsung.png",
        Reputation = new BrandReputation { Brand = name, Summary = "Reliable, strong local service." }
    };

    private static StoreInfo CompleteStore(string name = "SmartBuy") => new()
    {
        Name = name,
        Address = "Mecca St, Amman",
        Phone = "+962 6 000 0000",
        Rating = 4.5,
        ReviewCount = 320
    };

    private static AgentAnswer Answer(ProductSearchResult? products) => new()
    {
        Question = "ACs in Jordan",
        QueryType = QueryType.ProductResearch,
        Products = products
    };

    // ── Pass-through cases (nothing to validate or refill) ───────────────────────
    [Fact]
    public void NonProductAnswer_ScoresComplete_AndServesAsIs()
    {
        var report = Validator.Evaluate(Answer(products: null));

        report.Score.Should().Be(100);
        report.Decision.Should().Be(CacheDecision.ServeAsIs);
        report.Missing.Should().BeEmpty();
    }

    [Fact]
    public void EmptyProductResult_IsTreatedAsMiss()
    {
        // A product query that found nothing is treated as a cache MISS so it re-runs live: an empty
        // payload is too often a transient/upstream failure (provider outage, geo-filter bug) rather than
        // a true "nothing exists", and replaying it for the whole TTL would mask the recovery.
        var report = Validator.Evaluate(Answer(new ProductSearchResult()));

        report.Score.Should().BeLessThan(CacheQualityReport.MissThreshold);
        report.Decision.Should().Be(CacheDecision.Miss);
    }

    // ── Serve as-is ──────────────────────────────────────────────────────────────
    [Fact]
    public void FullyCompleteResult_ScoresHigh_AndServesAsIs()
    {
        var products = new ProductSearchResult
        {
            Models = new[] { CompleteProduct() },
            Brands = new[] { CompleteBrand() },
            Stores = new[] { CompleteStore() }
        };

        var report = Validator.Evaluate(Answer(products));

        report.Score.Should().Be(100);
        report.Gaps.Should().Be(CacheGap.None);
        report.Decision.Should().Be(CacheDecision.ServeAsIs);
        report.HasActionableGaps.Should().BeFalse();
    }

    // ── Reject (treat as a miss) ─────────────────────────────────────────────────
    [Fact]
    public void BarrenProduct_ScoresVeryLow_AndIsTreatedAsMiss()
    {
        var barren = new ProductModel { Name = "Mystery AC" }; // no image, price, specs, model, sku
        var report = Validator.Evaluate(Answer(new ProductSearchResult { Models = new[] { barren } }));

        report.Score.Should().BeLessThan(CacheQualityReport.MissThreshold);
        report.Decision.Should().Be(CacheDecision.Miss);
        report.Gaps.Should().HaveFlag(CacheGap.ProductImages);
        report.Gaps.Should().HaveFlag(CacheGap.ProductPrices);
        report.Gaps.Should().HaveFlag(CacheGap.Specs);
    }

    // ── Serve and enrich: products ───────────────────────────────────────────────
    [Fact]
    public void ProductMissingImageAndPrice_ServesAndEnriches_TargetingThatProduct()
    {
        var thin = new ProductModel
        {
            Name = "LG DualCool",
            Model = "DC18",
            // Has rich specs + model + SKU, but is missing the two card essentials (image, price) — a
            // genuinely mid-quality hit that should be shown now and refilled in the background.
            Specs = new Dictionary<string, string> { ["BTU"] = "18000", ["Energy"] = "A+", ["SKU"] = "DC18-JO" }
            // no image, no price
        };
        var report = Validator.Evaluate(Answer(new ProductSearchResult { Models = new[] { thin } }));

        report.Decision.Should().Be(CacheDecision.ServeAndEnrich);
        report.ThinProducts.Should().Equal(0);
        report.Gaps.Should().HaveFlag(CacheGap.ProductImages);
        report.Gaps.Should().HaveFlag(CacheGap.ProductPrices);
        report.Missing.Should().Contain(m => m.Contains("image"));
        report.Missing.Should().Contain(m => m.Contains("price"));
    }

    [Fact]
    public void CompleteProductIsNotFlaggedThin_OnlyTheIncompleteOne()
    {
        var products = new ProductSearchResult
        {
            Models = new[]
            {
                CompleteProduct("Complete"),                         // index 0 — fully enriched
                new ProductModel { Name = "Thin", Model = "T1" }     // index 1 — bare
            }
        };

        var report = Validator.Evaluate(Answer(products));

        // Only the incomplete product is a re-scrape target — that's what makes enrichment "partial".
        report.ThinProducts.Should().Equal(1);
    }

    // ── Serve and enrich: brands ─────────────────────────────────────────────────
    [Fact]
    public void BrandMissingLogoAndDescription_IsTargetedForReResearch()
    {
        var products = new ProductSearchResult
        {
            Models = new[] { CompleteProduct() },          // keep products perfect to isolate the brand gap
            Brands = new[] { new BrandInfo { Name = "Gree" } } // no logo, no reputation
        };

        var report = Validator.Evaluate(Answer(products));

        report.Decision.Should().Be(CacheDecision.ServeAndEnrich);
        report.DeficientBrands.Should().Equal("Gree");
        report.Gaps.Should().HaveFlag(CacheGap.BrandLogo);
        report.Gaps.Should().HaveFlag(CacheGap.BrandDescription);
    }

    [Fact]
    public void CompleteBrand_IsNotTargeted()
    {
        var products = new ProductSearchResult
        {
            Models = new[] { CompleteProduct() },
            Brands = new[] { CompleteBrand("Samsung"), new BrandInfo { Name = "NoName" } }
        };

        var report = Validator.Evaluate(Answer(products));

        report.DeficientBrands.Should().Equal("NoName");
    }

    // ── Serve and enrich: stores ─────────────────────────────────────────────────
    [Fact]
    public void StoreMissingLocationAndContact_IsTargetedForReVerification()
    {
        var products = new ProductSearchResult
        {
            Models = new[] { CompleteProduct() },
            Stores = new[] { new StoreInfo { Name = "Corner Shop" } } // no address/coords/phone/url/rating
        };

        var report = Validator.Evaluate(Answer(products));

        report.Decision.Should().Be(CacheDecision.ServeAndEnrich);
        report.DeficientStores.Should().Equal("Corner Shop");
        report.Gaps.Should().HaveFlag(CacheGap.StoreLocation);
        report.Gaps.Should().HaveFlag(CacheGap.StoreContact);
        report.Gaps.Should().HaveFlag(CacheGap.StoreMaps);
    }

    [Fact]
    public void StoreWithCoordinatesButNoAddress_CountsAsLocated()
    {
        var products = new ProductSearchResult
        {
            Models = new[] { CompleteProduct() },
            Stores = new[]
            {
                new StoreInfo
                {
                    Name = "GPS Store", Latitude = 31.95, Longitude = 35.91,
                    Phone = "+962 7 000", Rating = 4.0, ReviewCount = 10
                }
            }
        };

        var report = Validator.Evaluate(Answer(products));

        report.Gaps.Should().NotHaveFlag(CacheGap.StoreLocation);
        report.DeficientStores.Should().BeEmpty();
    }
}
