using Daleel.Web.Data;
using Daleel.Web.Pipeline.SiteSearch;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

// Intelligent site search — the fix for the gate audit's biggest finding: ProductSearchUrl hardcoded
// Shopify's /search?q= convention, so taghareedstore (the one Shopify store) was the only store that
// ever yielded items; WooCommerce stores (/?s=) got 404s, and soft-404 pages (HTTP 200, "404" body)
// were extracted as if they were results. Candidates try the LEARNED per-domain template first, then
// the known platform conventions; the judge rejects pages that are error/no-result shells so the
// harvest moves on to the next candidate instead of "extracting" nothing from a 404 page.
public class SiteSearchCandidatesTests
{
    [Fact]
    public void Without_a_profile_probes_the_known_platform_conventions_in_order()
    {
        var urls = SiteSearchCandidates.For("belmio.jo", "coffee machines", profile: null);

        urls.Should().Equal(
            "https://belmio.jo/search?q=coffee%20machines", // Shopify convention (the old only guess)
            "https://belmio.jo/?s=coffee%20machines");      // WordPress/WooCommerce convention
    }

    [Fact]
    public void A_learned_template_goes_first_and_probes_dedupe_against_it()
    {
        var profile = new SiteSearchProfile
        {
            Domain = "belmio.jo", SearchUrlTemplate = "https://belmio.jo/?s={query}"
        };

        var urls = SiteSearchCandidates.For("belmio.jo", "coffee machines", profile);

        urls.Should().Equal(
            "https://belmio.jo/?s=coffee%20machines",       // learned winner first
            "https://belmio.jo/search?q=coffee%20machines"); // remaining probe, deduped
    }

    [Fact]
    public void Query_is_escaped_and_blank_query_degrades_to_the_root()
    {
        SiteSearchCandidates.For("x.jo", "50% off & more", null)[0]
            .Should().Be("https://x.jo/search?q=50%25%20off%20%26%20more");
        SiteSearchCandidates.For("x.jo", " ", null).Should().Equal("https://x.jo");
    }

    [Fact]
    public void Template_for_reports_the_convention_a_winning_url_used()
    {
        SiteSearchCandidates.TemplateFor("https://belmio.jo/?s=coffee%20machines")
            .Should().Be("https://belmio.jo/?s={query}");
        SiteSearchCandidates.TemplateFor("https://x.jo/search?q=a%20b")
            .Should().Be("https://x.jo/search?q={query}");
    }
}

public class HarvestPageJudgeTests
{
    [Fact]
    public void A_soft_404_page_is_unusable()
    {
        // rokonbaghdad-jo.com returned HTTP 200 whose title says 404 — the old code "extracted" it.
        HarvestPageJudge.IsUsable("404 - تسوّق من ركن بغداد أفضل الأجهزة الكهربائية\nقائمة الفئات ...")
            .Should().BeFalse();
        HarvestPageJudge.IsUsable("Page Not Found - AOT Electronics Jordan\nGo back home")
            .Should().BeFalse();
    }

    [Fact]
    public void An_explicit_no_results_page_is_unusable_in_english_and_arabic()
    {
        HarvestPageJudge.IsUsable("Search results\nNo results found for \"coffee machines\". Try again.")
            .Should().BeFalse();
        HarvestPageJudge.IsUsable("نتائج البحث عن \"coffee machines\"\nنتائج البحث: coffee machines 0\nلا توجد نتائج")
            .Should().BeFalse();
    }

    [Fact]
    public void A_real_results_page_is_usable_even_when_it_mentions_results()
    {
        HarvestPageJudge.IsUsable(
                "Search results for \"coffee machines\"\n79 RESULTS FOUND FOR “COFFEE MACHINES”\n" +
                "Krups Sensation Milk EA912B10 Super-Automatic Coffee Machine — 399 JOD\nAdd to cart")
            .Should().BeTrue("counted results with products must never be mistaken for a no-results page");
    }

    [Fact]
    public void Empty_or_skeleton_content_is_unusable()
    {
        HarvestPageJudge.IsUsable(null).Should().BeFalse();
        HarvestPageJudge.IsUsable("").Should().BeFalse();
        HarvestPageJudge.IsUsable("Loading…").Should().BeFalse("a JS skeleton has nothing to extract");
    }
}
