using Daleel.Core.Models;
using Daleel.Web.Pipeline.Enrichment;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Pins the offer-verification facts, one page fetch each. Price: the priced line naming the model
/// wins as an EXACT figure; a page with prices that name nothing yields its leading price as
/// indicative. Relatedness: a page that isn't about the model fails the gate (its offer gets
/// removed). Condition: the page's explicit marker is truth, secondhand claims need positive
/// evidence. Description: the offer page's own prose fills "details", nav junk never does.
/// </summary>
public class OfferVerificationTests
{
    private static readonly ProductModel Dedica = new()
    {
        Name = "DeLonghi Espresso Maker Dedica Style", Brand = "DeLonghi", Model = "EC685"
    };

    // ── Price (unchanged PickPrice behavior) ────────────────────────────────────────────────

    [Fact]
    public void Line_naming_the_model_wins_as_exact()
    {
        var page = """
            Related: Moka pot classic 12.50 JOD
            DeLonghi Dedica Style EC685 espresso maker — 175.00 JOD in stock
            Delivery from 3.00 JOD
            """;

        var picked = OfferVerificationHandler.PickPrice(page, Dedica);

        picked.Should().NotBeNull();
        picked!.Value.Price.Should().Be(175.00m);
        picked.Value.Exact.Should().BeTrue("the priced line names the model");
    }

    [Fact]
    public void Unnamed_prices_fall_back_to_the_pages_leading_price_as_indicative()
    {
        var page = "Special offer today only: 99.00 JOD. Free delivery over 25.00 JOD.";

        var picked = OfferVerificationHandler.PickPrice(page, Dedica);

        picked.Should().NotBeNull();
        picked!.Value.Price.Should().Be(99.00m);
        picked.Value.Exact.Should().BeFalse("nothing on the page names the model — a lead, not a quote");
    }

    [Fact]
    public void No_prices_means_null()
    {
        OfferVerificationHandler.PickPrice("Beautiful espresso machines. Contact us for pricing.", Dedica)
            .Should().BeNull();
    }

    // ── Relatedness gate ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Related_product_page_passes_the_gate()
    {
        var page = """
            DeLonghi Dedica Style EC685 espresso machine
            Buy online with fast delivery across the kingdom.
            """;

        OfferVerificationHandler.IsRelatedPage(page, Dedica)
            .Should().BeTrue("the title region names the model");
    }

    [Fact]
    public void Unrelated_page_fails_the_gate()
    {
        var page = """
            Levi's Trucker Denim Jacket — classic fit
            A timeless denim jacket for every season. Free shipping on orders over 50 JOD.
            Sizes S through XXL available in three washes.
            """;

        OfferVerificationHandler.IsRelatedPage(page, Dedica)
            .Should().BeFalse("a denim-jacket page shares nothing with an espresso model");
    }

    [Fact]
    public void Tokens_buried_past_the_title_region_still_pass_when_three_share()
    {
        var page = new string('x', 700) + " DeLonghi Dedica EC685 available here.";

        OfferVerificationHandler.IsRelatedPage(page, Dedica)
            .Should().BeTrue("three shared tokens anywhere on the page suffice");
    }

    // ── Condition — page truth over listing guesses ─────────────────────────────────────────

    [Fact]
    public void Arabic_used_marker_reads_as_used()
    {
        OfferVerificationHandler.ExtractCondition("ماكينة قهوة مستعملة بحالة ممتازة للبيع")
            .Should().Be("used");
    }

    [Fact]
    public void Explicit_new_marker_reads_as_new()
    {
        OfferVerificationHandler.ExtractCondition("Condition: New. Original packaging, full warranty.")
            .Should().Be("new");
    }

    [Fact]
    public void Silent_page_carries_no_marker()
    {
        OfferVerificationHandler.ExtractCondition("Espresso machine with a 15 bar pump and milk frother.")
            .Should().BeNull();
    }

    [Fact]
    public void Unused_does_not_read_as_used()
    {
        OfferVerificationHandler.ExtractCondition("Unused surplus stock — every unit unopened and sealed.")
            .Should().BeNull("word boundaries keep 'unused' from hitting 'used'");
    }

    [Fact]
    public void Secondhand_marker_beats_a_new_mention_on_the_same_page()
    {
        OfferVerificationHandler.ExtractCondition("Buy new & used appliances at the best prices.")
            .Should().Be("used", "'new & used' pages sell used units");
    }

    [Fact]
    public void Silent_page_clears_an_unevidenced_used_claim()
    {
        OfferVerificationHandler.ApplyConditionEvidence("used", pageMarker: null)
            .Should().BeNull("secondhand needs positive evidence from the page");
    }

    [Fact]
    public void Page_marker_overrides_the_offers_guess()
    {
        OfferVerificationHandler.ApplyConditionEvidence("used", pageMarker: "new")
            .Should().Be("new", "the owner's live find: offer said Used, the page says new");
    }

    [Fact]
    public void Silent_page_leaves_a_non_secondhand_condition_alone()
    {
        OfferVerificationHandler.ApplyConditionEvidence("new", pageMarker: null).Should().Be("new");
    }

    // ── Description from the offer's own site ───────────────────────────────────────────────

    [Fact]
    public void Nav_junk_page_yields_no_description()
    {
        var page = """
            [Home](https://shop.example)[Offers](https://shop.example/offers)
            Login Cart Compare
            [Support](https://shop.example/support) [Contact](https://shop.example/contact)
            """;

        OfferVerificationHandler.ExtractDescription(page).Should().BeNull();
    }

    [Fact]
    public void Prose_page_yields_its_description()
    {
        var page = """
            [Home](https://shop.example) [Cart](https://shop.example/cart)
            The Dedica Style EC685 pulls rich espresso shots with a slim 15 cm footprint that fits any counter.
            Its thermoblock heats in forty seconds, and the adjustable frother steams milk for cappuccino at home.
            Login Cart Compare
            """;

        OfferVerificationHandler.ExtractDescription(page).Should().Be(
            "The Dedica Style EC685 pulls rich espresso shots with a slim 15 cm footprint that fits any counter.\n" +
            "Its thermoblock heats in forty seconds, and the adjustable frother steams milk for cappuccino at home.");
    }

    [Fact]
    public void Description_is_capped_at_two_thousand_chars()
    {
        var line = string.Join(" ", Enumerable.Repeat("solid espresso engineering with care.", 45));
        var page = line + "\n" + line;

        var description = OfferVerificationHandler.ExtractDescription(page);

        description.Should().NotBeNull();
        description!.Length.Should().BeLessThanOrEqualTo(2000);
        description.Length.Should().BeGreaterThan(1500, "the cap truncates, it doesn't reject");
    }

    // ── Junk check for an existing "details" blob ───────────────────────────────────────────

    [Fact]
    public void Menu_shaped_details_count_as_junk()
    {
        OfferVerificationHandler.IsJunkDetails("[Home](https://x)[Hot Offers](https://x)\nLogin Compare")
            .Should().BeTrue();
    }

    [Fact]
    public void Real_prose_details_are_not_junk()
    {
        OfferVerificationHandler.IsJunkDetails(
                "The Dedica Style EC685 pulls rich espresso shots with a slim footprint. " +
                "Its thermoblock heats in forty seconds, and the frother steams milk for cappuccino at home.")
            .Should().BeFalse();
    }
}
