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
