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

    [Theory]
    // Arabic terms must match as WORDS, not substrings — 'بار' (bar) lives inside many
    // innocent words. The first case is a real production false positive: a Facebook ad for a
    // dehumidifier ("…dust, mold, wall sweating…") was removed as alcohol.
    [InlineData("أفضل جهاز للتخلص من الرطوبة والعفن والغبار وتعرق الجدران")] // dust
    [InlineData("آخر أخبار الرياضة اليوم")]     // news
    [InlineData("مشروب بارد منعش")]             // cold drink
    [InlineData("مبارك عليكم الشهر")]            // congratulations
    [InlineData("اختبار الجهاز قبل الشراء")]     // testing
    [InlineData("عداد كهرباء ذكي")]              // electricity meter (no بار at all — control)
    public void IsHalal_ArabicSubstringsOfInnocentWords_DoNotMatch(string text)
    {
        _strict.IsHalal(text).Should().BeTrue();
    }

    [Theory]
    // …while the standalone word (with or without attached clitics) still matches.
    [InlineData("رحنا إلى البار مساء أمس")]      // "the bar" — definite article
    [InlineData("بار ومطعم في وسط البلد")]       // bare word
    [InlineData("سهرة في بار الفندق")]            // bar of the hotel
    public void IsHalal_ArabicStandaloneBar_StillMatches(string text)
    {
        _strict.IsHalal(text).Should().BeFalse();
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

    [Theory]
    // A store's financing model is not haram content: banks and retailers that offer interest-based
    // (riba) installment plans must stay in results — the user can always pay cash. See the policy
    // note in ContentFilter.Categories.
    [InlineData("Arab Bank — personal loans")]
    [InlineData("Smart Electronics — 0% interest installments available")]
    [InlineData("Cairo Amman Bank mortgage rates")]
    [InlineData("بنك الإسكان للتجارة والتمويل")]      // Housing Bank for Trade & Finance
    [InlineData("تقسيط بفائدة على الأجهزة")]          // installments with interest on appliances
    public void IsHalal_DoesNotFilterBanksOrInterestFinancing(string text)
    {
        // Even at the strictest level a store is judged by what it sells, not how it finances it.
        _strict.IsHalal(text).Should().BeTrue();
    }

    [Fact]
    public void FilterStores_KeepsBankButRemovesLiquorStore()
    {
        var stores = new List<StoreLocation>
        {
            new() { Name = "Arab Bank", Address = "King Hussein St" },          // riba financing — allowed
            new() { Name = "Smart Electronics (installments)", Address = "Mecca St" }, // allowed
            new() { Name = "The Liquor Store", Address = "Rainbow St" }          // haram content — removed
        };

        var kept = _strict.FilterStores(stores);

        kept.Should().HaveCount(2);
        kept.Should().Contain(s => s.Name == "Arab Bank");
        kept.Should().NotContain(s => s.Name.Contains("Liquor"));
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
