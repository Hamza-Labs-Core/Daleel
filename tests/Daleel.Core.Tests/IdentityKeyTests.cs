using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests;

/// <summary>
/// The save-time convergence key: listings of the SAME physical product must collide; different
/// products must not — a wrong merge is worse than a duplicate.
/// </summary>
public class IdentityKeyTests
{
    // ── Must collide ─────────────────────────────────────────────────────────

    [Fact]
    public void Model_separator_variants_collide()
    {
        var a = StableId.IdentityKeyFor("jordan", "TCL", "TAC-24CHSD/TPH11I", "TCL split AC");
        var b = StableId.IdentityKeyFor("jordan", "TCL", "TAC 24CHSD TPH11I", "TCL inverter air conditioner");
        var c = StableId.IdentityKeyFor("jordan", "tcl", "tac24chsdtph11i", "مكيف تي سي ال");

        b.Should().Be(a);
        c.Should().Be(a, "the SKU decides — names (even cross-language) don't matter once a model exists");
    }

    [Fact]
    public void Name_token_order_and_marketing_filler_collide()
    {
        var a = StableId.IdentityKeyFor("jordan", "Gree", null, "Gree AC 12000");
        var b = StableId.IdentityKeyFor("jordan", "Gree", null, "AC Gree 12000 — best price, free delivery!");
        var c = StableId.IdentityKeyFor("jordan", null, null, "12000 AC Gree original");

        b.Should().Be(a);
        c.Should().Be(a, "the brand token in the NAME converges with the brand FIELD");
    }

    [Fact]
    public void Model_beats_name_drift()
    {
        var a = StableId.IdentityKeyFor("jordan", "Samsung", "AR18TXHQ", "Samsung 1.5 ton inverter AC");
        var b = StableId.IdentityKeyFor("jordan", "Samsung", "AR18TXHQ", "سامسونج مكيف انفرتر");

        b.Should().Be(a);
    }

    // ── Must NOT collide ─────────────────────────────────────────────────────

    [Fact]
    public void Different_models_of_the_same_brand_stay_apart()
    {
        var a = StableId.IdentityKeyFor("jordan", "Samsung", "AR12TXHQ", "Samsung AC");
        var b = StableId.IdentityKeyFor("jordan", "Samsung", "AR18TXHQ", "Samsung AC");

        b.Should().NotBe(a);
    }

    [Fact]
    public void Different_capacities_in_the_name_stay_apart()
    {
        var a = StableId.IdentityKeyFor("jordan", "Gree", null, "Gree AC 12000");
        var b = StableId.IdentityKeyFor("jordan", "Gree", null, "Gree AC 18000");

        b.Should().NotBe(a, "the capacity token is a real distinguisher, not filler");
    }

    [Fact]
    public void Name_only_identity_is_scoped_per_market()
    {
        var jo = StableId.IdentityKeyFor("jordan", null, null, "Generic desk fan 40cm");
        var us = StableId.IdentityKeyFor("usa", null, null, "Generic desk fan 40cm");

        us.Should().NotBe(jo, "a name-only identity is only trustworthy within one market");
    }

    [Fact]
    public void Cross_language_names_without_a_model_stay_apart()
    {
        // Deliberate: zero shared tokens = no hash collision. Converging these requires judgment
        // (vision/LLM in the dedup worker), never the hash.
        var en = StableId.IdentityKeyFor("jordan", null, null, "Samsung inverter air conditioner");
        var ar = StableId.IdentityKeyFor("jordan", null, null, "سامسونج مكيف انفرتر");

        ar.Should().NotBe(en);
    }

    [Fact]
    public void Routing_id_is_unchanged_by_the_identity_key()
    {
        // /product/{id} links must keep working: IdentityKeyFor must not affect ForProduct.
        StableId.ForProduct("Gree", "GWC12", "x").Should().Be(StableId.ForProduct("Gree", "GWC12", "y"));
    }
}
