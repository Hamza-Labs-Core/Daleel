using Daleel.Core.Models;
using Daleel.Web.Pipeline.Enrichment;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// The critical fix from the branch review: whole-list enrichment units (vision, conditions,
/// catalogue, brand-DB fill, cache refill) must COMPOSE onto the row-locked live models, never
/// assign a whole list built from their pre-network snapshot — which silently reverted every
/// concurrent unit's committed patch. MergeAdditive is the shared composition; these pin that it
/// only ADDS (fills empty fields, appends offers/models) and never reverts.
/// </summary>
public class PatchCompositionTests
{
    private static ProductModel Model(string name, string? image = null, params PriceOffer[] offers) =>
        new() { Name = name, ImageUrl = image, Offers = offers.ToList() };

    private static PriceOffer Offer(string source, string? url = null, decimal? price = null, string? condition = null) =>
        new() { Source = source, Url = url, Price = price, Condition = condition };

    [Fact]
    public void Concurrent_unit_patch_does_not_revert_a_committed_offer()
    {
        // The exact race: unit A committed an offer to M1 (now "live"); unit B, whose transform was
        // built from the T0 snapshot WITHOUT A's offer, must not erase it.
        var live = new List<ProductModel>
        {
            Model("M1", image: "https://img/a", Offer("StoreA", "https://a.jo/p1", 100m))
        };
        var transformFromStaleSnapshot = new List<ProductModel>
        {
            Model("M1", offers: Offer("StoreB", "https://b.jo/p1", 200m)) // B's addition only
        };

        var merged = HandlerHelpers.MergeAdditive(live, transformFromStaleSnapshot, out var changed);

        changed.Should().BeTrue();
        var m1 = merged.Single(m => m.Name == "M1");
        m1.ImageUrl.Should().Be("https://img/a", "A's committed image survives B's stale snapshot");
        m1.Offers.Select(o => o.Source).Should().BeEquivalentTo(new[] { "StoreA", "StoreB" },
            "both offers present — B appended, A not reverted");
    }

    [Fact]
    public void Fills_empty_fields_and_completes_matching_offers_never_overwrites()
    {
        var live = new List<ProductModel>
        {
            Model("M1", image: null, Offer("StoreA", "https://a.jo/p1", price: null))
        };
        var incoming = new List<ProductModel>
        {
            new()
            {
                Name = "M1", ImageUrl = "https://img/new",
                Specs = new Dictionary<string, string> { ["ton"] = "2" },
                Offers = new List<PriceOffer> { Offer("StoreA", "https://a.jo/p1", price: 150m, condition: "new") }
            }
        };

        var merged = HandlerHelpers.MergeAdditive(live, incoming, out var changed);

        changed.Should().BeTrue();
        var m1 = merged.Single(m => m.Name == "M1");
        m1.ImageUrl.Should().Be("https://img/new", "empty image gets filled");
        m1.Specs["ton"].Should().Be("2");
        var offer = m1.Offers.Single();
        offer.Price.Should().Be(150m, "the null-priced offer with the same url gets its price filled");
        offer.Condition.Should().Be("new");
    }

    [Fact]
    public void Existing_value_is_never_stomped_by_a_later_unit()
    {
        var live = new List<ProductModel>
        {
            new()
            {
                Name = "M1", ImageUrl = "https://img/keep",
                Specs = new Dictionary<string, string> { ["ton"] = "2" },
                Offers = new List<PriceOffer> { Offer("StoreA", "https://a.jo/p1", price: 100m) }
            }
        };
        var incoming = new List<ProductModel>
        {
            new()
            {
                Name = "M1", ImageUrl = "https://img/other",
                Specs = new Dictionary<string, string> { ["ton"] = "3" }, // conflicting — must NOT win
                Offers = new List<PriceOffer> { Offer("StoreA", "https://a.jo/p1", price: 999m) }
            }
        };

        var merged = HandlerHelpers.MergeAdditive(live, incoming, out var changed);

        changed.Should().BeFalse("nothing was empty to fill; conflicting values never overwrite");
        var m1 = merged.Single(m => m.Name == "M1");
        m1.ImageUrl.Should().Be("https://img/keep");
        m1.Specs["ton"].Should().Be("2");
        m1.Offers.Single().Price.Should().Be(100m);
    }

    [Fact]
    public void New_models_from_the_transform_are_appended_untouched_models_kept()
    {
        var live = new List<ProductModel> { Model("M1"), Model("M2") };
        var incoming = new List<ProductModel>
        {
            Model("M1"),                             // unchanged
            Model("MEC AC 3 Ton", offers: Offer("Store", "https://s/3ton", 1250m)) // a discovery
        };

        var merged = HandlerHelpers.MergeAdditive(live, incoming, out var changed);

        changed.Should().BeTrue();
        merged.Select(m => m.Name).Should().BeEquivalentTo(new[] { "M1", "M2", "MEC AC 3 Ton" },
            "M2 (absent from the transform) is kept; the new SKU is appended");
    }

    [Fact]
    public void Duplicate_identity_discoveries_are_not_double_appended()
    {
        var live = new List<ProductModel>
        {
            new() { Name = "DeLonghi Dedica", Brand = "DeLonghi", Model = "EC685" }
        };
        var incoming = new List<ProductModel>
        {
            new() { Name = "DeLonghi Dedica Style EC685", Brand = "DeLonghi", Model = "EC685" } // same identity
        };

        var merged = HandlerHelpers.MergeAdditive(live, incoming, out var changed);

        changed.Should().BeFalse("same Brand+Model identity — not a new product");
        merged.Should().HaveCount(1);
    }
}
