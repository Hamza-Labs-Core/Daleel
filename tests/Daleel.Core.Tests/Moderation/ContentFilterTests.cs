using Daleel.Core.Models;
using Daleel.Core.Moderation;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Moderation;

public class ContentFilterTests
{
    private readonly ContentFilter _strict = new(FilterStrictness.Strict);

    [Theory]
    [InlineData("Heineken Beer 330ml")]
    [InlineData("Premium Whisky tasting")]
    [InlineData("City Bar & Lounge")]
    [InlineData("Best wine deals this week")]
    public void IsHalal_FlagsAlcoholEnglish(string text)
    {
        _strict.IsHalal(text).Should().BeFalse();
    }

    [Theory]
    [InlineData("بيرة مستوردة")]      // imported beer
    [InlineData("متجر خمور في عمّان")] // liquor store in Amman
    [InlineData("لحم خنزير مدخن")]     // smoked pork
    public void IsHalal_FlagsArabicTerms(string text)
    {
        _strict.IsHalal(text).Should().BeFalse();
    }

    [Theory]
    [InlineData("Samsung air conditioner")]
    [InlineData("أفضل مكيف في الأردن")]
    [InlineData("Barber shop downtown")]  // must NOT match "bar"
    public void IsHalal_AllowsCleanContent(string text)
    {
        _strict.IsHalal(text).Should().BeTrue();
    }

    [Fact]
    public void FilterSearchResults_RemovesAlcoholHits()
    {
        // Modeled on a SearchResult's filterable text (title/snippet/seller) via the generic filter.
        var results = new[]
        {
            new { Title = "Samsung Fridge", Text = "energy efficient" },
            new { Title = "Imported Beer 6-pack", Text = "cold lager" }
        };

        var kept = _strict.FilterResults(results, r => $"{r.Title} {r.Text}");

        kept.Should().ContainSingle().Which.Title.Should().Be("Samsung Fridge");
    }

    [Fact]
    public void FilterDeals_RemovesPorkProducts()
    {
        var deals = new List<DealListing>
        {
            new() { Title = "Chicken breast 1kg", Product = "chicken" },
            new() { Title = "Smoked Bacon offer", Product = "pork bacon" },
            new() { Title = "Olive oil 2L", Product = "olive oil" }
        };

        var kept = _strict.FilterDeals(deals);

        kept.Should().HaveCount(2);
        kept.Should().NotContain(d => d.Title.Contains("Bacon"));
    }

    [Fact]
    public void FilterStores_RemovesBarsAndLiquorStores()
    {
        var stores = new List<StoreLocation>
        {
            new() { Name = "Smart Electronics", Address = "Mecca St" },
            new() { Name = "The Liquor Store", Address = "Rainbow St" },
            new() { Name = "Downtown Sports Bar", Address = "Abdoun" },
            new() { Name = "خمارة المدينة", Address = "وسط البلد" } // "city wine bar" (Arabic)
        };

        var kept = _strict.FilterStores(stores);

        kept.Should().ContainSingle().Which.Name.Should().Be("Smart Electronics");
    }

    [Fact]
    public void Moderate_DoesNotBlockTobacco_ButStrictDoes()
    {
        const string text = "Marlboro cigarettes carton";

        new ContentFilter(FilterStrictness.Moderate).IsHalal(text).Should().BeTrue();
        new ContentFilter(FilterStrictness.Strict).IsHalal(text).Should().BeFalse();
    }

    [Fact]
    public void Off_DisablesAllFiltering()
    {
        var off = new ContentFilter(FilterStrictness.Off);
        off.IsHalal("Beer and pork and gambling").Should().BeTrue();
        off.FilterText("Whisky bar").Should().Be("Whisky bar");
    }

    [Fact]
    public void AuditLog_RecordsRemovals_ButNeverContentItself()
    {
        var deals = new List<DealListing>
        {
            new() { Title = "Beer crate", Product = "beer" },
            new() { Title = "Rice 5kg", Product = "rice" }
        };

        _strict.FilterDeals(deals);

        _strict.AuditLog.Should().ContainSingle();
        _strict.AuditLog[0].Should().Contain("alcohol"); // category, not the raw title
    }
}
