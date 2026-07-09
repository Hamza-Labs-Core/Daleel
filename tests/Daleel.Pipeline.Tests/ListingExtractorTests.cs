using System.Text.Json;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Pipeline.Extraction;
using Daleel.Search.Abstractions;
using FluentAssertions;
using Xunit;

namespace Daleel.Pipeline.Tests;

public class ListingExtractorTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void FromExtractedJson_ParsesProductsArray()
    {
        var json = Json("""
        {
          "products": [
            {
              "name": "Samsung Split AC AR24",
              "brand": "Samsung",
              "model": "AR24TXHQ",
              "price": 450,
              "currency": "JOD",
              "url": "https://opensooq.com/listing/1",
              "image_url": "https://img/1.jpg",
              "specs": { "cooling_capacity": "24000 BTU", "energy_rating": "A++" },
              "availability": "in stock",
              "seller": "CoolStore",
              "condition": "New"
            }
          ]
        }
        """);

        var listings = ListingExtractor.FromExtractedJson(json, "OpenSooq", ResultType.Marketplace);

        listings.Should().ContainSingle();
        var l = listings[0];
        l.Name.Should().Be("Samsung Split AC AR24");
        l.Brand.Should().Be("Samsung");
        l.Model.Should().Be("AR24TXHQ");
        l.Price.Should().Be(450);
        l.Currency.Should().Be("JOD");
        l.Source.Should().Be("OpenSooq");
        l.SourceType.Should().Be(ResultType.Marketplace);
        l.Specs.Should().ContainKey("cooling_capacity").WhoseValue.Should().Be("24000 BTU");
        l.Condition.Should().Be("new");
    }

    [Fact]
    public void FromExtractedJson_ParsesReviews()
    {
        var json = Json("""
        {
          "products": [
            {
              "name": "Samsung AR24",
              "url": "https://store.com/p/ar24",
              "reviews": [
                { "text": "Cools fast, quiet.", "rating": 5, "author": "Ahmad" },
                { "text": "Good value", "rating": 4 },
                { "rating": 3 }
              ]
            }
          ]
        }
        """);

        var listings = ListingExtractor.FromExtractedJson(json, "Store", ResultType.Marketplace);

        var reviews = listings.Should().ContainSingle().Subject.RatedReviews;
        reviews.Should().HaveCount(2); // the review with no text is skipped
        reviews[0].Text.Should().Be("Cools fast, quiet.");
        reviews[0].Rating.Should().Be(5);
        reviews[0].Author.Should().Be("Ahmad");
        reviews[1].Rating.Should().Be(4);
    }

    [Fact]
    public void FromExtractedJson_ParsesStringPriceAndBareArray()
    {
        var json = Json("""
        [ { "name": "Midea AC", "price": "399.00 JOD", "url": "https://x/2" } ]
        """);

        var listings = ListingExtractor.FromExtractedJson(json, "Carrefour", ResultType.StorePage, "JOD");

        listings.Should().ContainSingle();
        listings[0].Price.Should().Be(399.00m);
        listings[0].Currency.Should().Be("JOD"); // backfilled from default
    }

    [Fact]
    public void FromExtractedJson_SkipsEmptyRows()
    {
        var json = Json("""{ "products": [ { }, { "name": "" } ] }""");
        ListingExtractor.FromExtractedJson(json, "X", ResultType.Marketplace).Should().BeEmpty();
    }

    [Fact]
    public void FromExtractedJson_RejectsUrlShapedName_FallsBackToModel()
    {
        // Extractors sometimes drop the source URL/bare domain into the name field. A URL-shaped name must
        // never surface as the product name — fall back to the model number instead ("links as names" bug).
        var json = Json("""
        {
          "products": [
            { "name": "https://amazon.com/dp/B0ABC", "model": "AR24TXHQ", "price": 450, "url": "https://amazon.com/dp/B0ABC" },
            { "name": "www.opensooq.com/split-ac", "price": 300, "url": "https://opensooq.com/x" }
          ]
        }
        """);

        var listings = ListingExtractor.FromExtractedJson(json, "X", ResultType.Marketplace);

        // First row falls back to the model number; second row has only a URL for a name → dropped entirely.
        listings.Should().ContainSingle();
        listings[0].Name.Should().Be("AR24TXHQ");
        listings.Should().NotContain(l => l.Name.Contains("http") || l.Name.Contains("www."));
    }

    [Theory]
    // A store CATEGORY page scraped via CF Browser returns the whole markdown product card as the
    // "name": a link whose anchor text is [badge **Product Name** price sizes]. Unwrap the link,
    // prefer the bolded run (the real name), and drop the badge/price/size noise.
    [InlineData(
        "[top rated\\ \\ **Cosmo pant in Gramercy linen blend**\\ \\ $148\\ \\ select colors $81.99\\ \\ Classic, Petite, Tall](https://www.jcrew.com/p/womens/pants/cosmo-pant/CS231?color_name=black)",
        "Cosmo pant in Gramercy linen blend")]
    [InlineData("**Soleil pant in linen**", "Soleil pant in linen")]
    // A clean name is returned unchanged.
    [InlineData("Women's Legendary Chino Pant", "Women's Legendary Chino Pant")]
    public void CleanExtractedName_UnwrapsMarkdownCards(string raw, string expected) =>
        ListingExtractor.CleanExtractedName(raw).Should().Be(expected);

    [Theory]
    [InlineData("https://www.jcrew.com/p/x")]        // URL-shaped
    [InlineData("www.foo.io")]                       // bare domain
    [InlineData("Women's Pants | Find the Lowest Prices")] // search/SEO page title
    [InlineData("")]
    [InlineData(null)]
    public void CleanExtractedName_DropsNoise(string? raw) =>
        ListingExtractor.CleanExtractedName(raw).Should().BeNull();

    [Fact]
    public void FromExtractedJson_CleansMarkdownBlobNames_AndVariantsCollapse()
    {
        // Store category pages (J.Crew) scraped via CF Browser return the whole markdown product card
        // as the "name"; the color variants differ only by the URL. After cleaning, the variants share
        // one product name and MUST aggregate to a single model — they were surfacing as many garbage
        // "[top rated **Cosmo pant**…](url)" grid cards, one per colour (live bug, "women pants" job 21).
        var json = Json("""
        {
          "products": [
            { "name": "[top rated\\ \\ **Cosmo pant in linen**\\ \\ $148](https://jcrew.com/p/cosmo/CS231?color=black)", "url": "https://jcrew.com/p/cosmo/CS231?color=black" },
            { "name": "[best seller\\ \\ **Cosmo pant in linen**\\ \\ $148](https://jcrew.com/p/cosmo/CS231?color=navy)", "url": "https://jcrew.com/p/cosmo/CS231?color=navy" }
          ]
        }
        """);

        var listings = ListingExtractor.FromExtractedJson(json, "jcrew.com", ResultType.StorePage);

        listings.Should().HaveCount(2);
        listings.Should().OnlyContain(l => l.Name == "Cosmo pant in linen");
        // The whole point: cleaned names share a dedup key, so the aggregator collapses them to one.
        ListingExtractor.Merge(listings).Should().ContainSingle();
    }

    [Fact]
    public void FromExtractedJson_DropsSearchAndCategoryPages()
    {
        // Store search/category pages sometimes slip into the extractor's product array. They are crawl
        // entry points, not individual items — drop them; keep the real product page alongside.
        var json = Json("""
        {
          "products": [
            { "name": "Women's Pants", "url": "https://store.com/search?q=women+pants" },
            { "name": "Pants Collection", "url": "https://store.com/collections/womens-pants" },
            { "name": "Cosmo Pant in Linen", "url": "https://store.com/products/cosmo-pant-CS231" }
          ]
        }
        """);

        var listings = ListingExtractor.FromExtractedJson(json, "store.com", ResultType.StorePage);

        listings.Should().ContainSingle().Which.Name.Should().Be("Cosmo Pant in Linen");
    }

    [Fact]
    public void FromShopping_SkipsUrlShapedTitles()
    {
        // A shopping hit titled with a bare domain ("atelier21.org") has no product identity —
        // it must never surface as a product card (seen live on QA, "best microwave oven").
        var hits = new[]
        {
            new SearchResult { Title = "atelier21.org", Price = new Money(99, "USD"), Url = "https://atelier21.org/x" },
            new SearchResult { Title = "www.foo-shop.com/deals", Price = new Money(10, "USD") },
            new SearchResult { Title = "Midea 0.7 Cu Ft Microwave", Price = new Money(75, "USD") },
        };

        var listings = ListingExtractor.FromShopping(hits);

        listings.Should().ContainSingle().Which.Name.Should().Be("Midea 0.7 Cu Ft Microwave");
    }

    [Fact]
    public void FromShopping_MapsHitsToListings()
    {
        var hits = new[]
        {
            new SearchResult { Title = "LG Dual Inverter AC", Price = new Money(520, "JOD"), Seller = "Xcite", Url = "https://x/1" },
            new SearchResult { Title = "", Price = null }, // dropped
        };

        var listings = ListingExtractor.FromShopping(hits);

        listings.Should().ContainSingle();
        listings[0].Brand.Should().Be("LG");
        listings[0].Price.Should().Be(520);
        listings[0].Seller.Should().Be("Xcite");
    }

    [Fact]
    public void Merge_DeduplicatesByModel()
    {
        var fromOpenSooq = new[]
        {
            new ProductListing { Name = "Samsung AR24 (used)", Brand = "Samsung", Model = "AR24TXHQ", Price = 380, Currency = "JOD", Source = "OpenSooq" }
        };
        var fromCarrefour = new[]
        {
            new ProductListing { Name = "Samsung AR24 Split", Brand = "Samsung", Model = "ar24txhq", Price = 460, Currency = "JOD", Source = "Carrefour" },
            new ProductListing { Name = "LG S4 1.5HP", Brand = "LG", Model = "S4-Q12", Price = 410, Currency = "JOD", Source = "Carrefour" }
        };

        var merged = ListingExtractor.Merge(fromOpenSooq, fromCarrefour);

        // Same model (case-insensitive) collapses; the LG one stays. First-seen wins.
        merged.Should().HaveCount(2);
        merged.Should().Contain(l => l.Source == "OpenSooq" && l.Model == "AR24TXHQ");
        merged.Should().Contain(l => l.Brand == "LG");
        merged.Should().NotContain(l => l.Source == "Carrefour" && l.Brand == "Samsung");
    }

    [Fact]
    public void Merge_FallsBackToNameWhenNoModel()
    {
        var a = new[] { new ProductListing { Name = "Generic Cooler 12000 BTU", Source = "A" } };
        var b = new[] { new ProductListing { Name = "Generic Cooler 12000 BTU", Source = "B" } };

        ListingExtractor.Merge(a, b).Should().ContainSingle();
    }
}
