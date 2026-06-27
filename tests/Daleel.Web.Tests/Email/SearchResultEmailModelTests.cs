using Daleel.Agent;
using Daleel.Core.Models;
using Daleel.Web.Email;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Email;

/// <summary>
/// The mapping from a completed <see cref="AgentAnswer"/> to the email view-model: top-3 truncation,
/// price-range formatting from a model's offers, and graceful handling of a non-product answer.
/// </summary>
public class SearchResultEmailModelTests
{
    private static ProductModel Model(string name, string? image, params (decimal amount, string ccy)[] offers) =>
        new()
        {
            Name = name,
            ImageUrl = image,
            Offers = offers.Select(o => new PriceOffer { Source = "s", Price = o.amount, Currency = o.ccy }).ToList()
        };

    [Fact]
    public void From_MapsCountsAndCta()
    {
        var answer = new AgentAnswer
        {
            Question = "  air conditioners  ",
            Products = new ProductSearchResult
            {
                Models = new[] { Model("A", null, (100m, "JOD")) },
                Brands = new[] { new BrandInfo { Name = "Gree" }, new BrandInfo { Name = "LG" } },
                Stores = new[] { new StoreInfo { Name = "Smart Buy", Address = "Amman" } }
            }
        };

        var model = SearchResultEmailModel.From(answer, "en", "https://x/history");

        model.Query.Should().Be("air conditioners"); // trimmed
        model.ProductCount.Should().Be(1);
        model.BrandCount.Should().Be(2);
        model.StoreCount.Should().Be(1);
        model.CtaUrl.Should().Be("https://x/history");
    }

    [Fact]
    public void From_TakesAtMostThreeProductsAndStores()
    {
        var answer = new AgentAnswer
        {
            Products = new ProductSearchResult
            {
                Models = Enumerable.Range(1, 5).Select(i => Model($"M{i}", null, (i * 10m, "JOD"))).ToList(),
                Stores = Enumerable.Range(1, 5).Select(i => new StoreInfo { Name = $"S{i}" }).ToList()
            }
        };

        var model = SearchResultEmailModel.From(answer, "ar", "https://x");

        model.TopProducts.Should().HaveCount(3);
        model.TopStores.Should().HaveCount(3);
        model.TopProducts[0].Name.Should().Be("M1");
        model.IsRtl.Should().BeTrue();
    }

    [Fact]
    public void From_FormatsPriceRangeFromOffers()
    {
        var answer = new AgentAnswer
        {
            Products = new ProductSearchResult
            {
                Models = new[]
                {
                    Model("Range", null, (450m, "JOD"), (320m, "JOD")),  // low–high
                    Model("Single", null, (599m, "JOD")),               // one price
                    Model("NoPrice", null)                               // no offers
                }
            }
        };

        var model = SearchResultEmailModel.From(answer, "en", "https://x");

        model.TopProducts[0].PriceRange.Should().Be("320 JD – 450 JD");
        model.TopProducts[1].PriceRange.Should().Be("599 JD");
        model.TopProducts[2].PriceRange.Should().BeNull();
    }

    [Fact]
    public void From_NonProductAnswer_HasZeroCountsAndEmptyLists()
    {
        var answer = new AgentAnswer { Question = "how does inflation work", Products = null };

        var model = SearchResultEmailModel.From(answer, "en", "https://x");

        model.ProductCount.Should().Be(0);
        model.TopProducts.Should().BeEmpty();
        model.TopStores.Should().BeEmpty();
    }
}
