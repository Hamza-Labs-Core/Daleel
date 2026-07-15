using Bunit;
using Daleel.Core.Models;
using Daleel.Web.Components.Shared;
using Daleel.Web.Services;
using Daleel.Web.Translation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// bUnit tests for the search-object-driven grid: goal-driven default sort (incl. the new
/// "rating" ordering) and clean fallback when the result carries no strategy.
/// Setup mirrors ComponentRenderTests (Mud services + localization + loose JS), plus fakes for
/// the two extra services ModelCard's tree resolves (relevance-feedback flagging + current user).
/// </summary>
public class ProductListingsGridTests : TestContext
{
    public ProductListingsGridTests()
    {
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddSingleton<ITranslationService>(new NoTranslation());
        Services.AddSingleton<IRelevanceFeedbackService>(new NoRelevanceFeedback());
        Services.AddSingleton<ICurrentUser>(new AnonymousUser());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class NoTranslation : ITranslationService
    {
        public bool Enabled => false;
        public Task<string> TranslateAsync(string text, string targetLang, CancellationToken ct = default)
            => Task.FromResult(text);
        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
            => Task.FromResult(texts);
    }

    /// <summary>ModelCard injects this to record "not relevant" flags — a no-op is fine, these
    /// tests never click the flag button.</summary>
    private sealed class NoRelevanceFeedback : IRelevanceFeedbackService
    {
        public Task RecordAsync(
            ProductModel model, string query, string? target, string? geo, string? reason, string? userId,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>ModelCard injects this to gate the flag button — an always-signed-out user is fine,
    /// these tests never click it.</summary>
    private sealed class AnonymousUser : ICurrentUser
    {
        public Task<string?> GetUserIdAsync() => Task.FromResult<string?>(null);
        public Task<bool> IsAuthenticatedAsync() => Task.FromResult(false);
        public Task<string?> GetDisplayNameAsync() => Task.FromResult<string?>(null);
        public Task<bool> IsAdminAsync() => Task.FromResult(false);
    }

    private static ProductModel Model(string name, decimal? price = null, double? rating = null, int? ratingCount = null) =>
        new()
        {
            Name = name,
            Brand = "B",
            Rating = rating,
            RatingCount = ratingCount,
            Offers = price is { } p
                ? new[] { new PriceOffer { Source = "s", Price = p, Currency = "JOD" } }
                : Array.Empty<PriceOffer>()
        };

    private static ProductSearchResult Result(SearchStrategy? strategy, params ProductModel[] models) =>
        Result(strategy, "q", models);

    private static ProductSearchResult Result(SearchStrategy? strategy, string query, params ProductModel[] models) =>
        new() { Query = query, Geo = "jordan", Country = "Jordan", Models = models, Strategy = strategy };

    private static ProductModel WithSpec(string name, string key, string value, decimal price = 100m)
    {
        var m = Model(name, price);
        return m with { Specs = new Dictionary<string, string> { [key] = value } };
    }

    // MudSelect (the sort/filter dropdowns) resolves popovers through MudPopoverProvider at render
    // time — render it alongside the grid, same as MudBlazor's own component-test setup requires.
    private IRenderedComponent<ProductListings> Render(ProductSearchResult result)
    {
        RenderComponent<MudPopoverProvider>();
        return RenderComponent<ProductListings>(p => p.Add(x => x.Result, result));
    }

    private static IReadOnlyList<string> CardOrder(IRenderedComponent<ProductListings> cut, params string[] names)
    {
        // Guard the ordering against vacuous passes: a missing name gets IndexOf -1 and would
        // sort FIRST — exactly the position these tests assert.
        foreach (var n in names)
        {
            cut.Markup.Should().Contain(n, "every asserted card must actually render");
        }
        return names.OrderBy(n => cut.Markup.IndexOf(n, StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void DefaultSort_FromStrategy_OrdersByPriceAscending()
    {
        var cut = Render(Result(
            new SearchStrategy { DefaultSort = "price_asc" },
            Model("Expensive", 900m), Model("Cheap", 100m), Model("Middle", 400m)));

        CardOrder(cut, "Expensive", "Cheap", "Middle")
            .Should().ContainInOrder("Cheap", "Middle", "Expensive");
    }

    [Fact]
    public void RatingSort_OrdersByRatingDesc_NullsLast()
    {
        var cut = Render(Result(
            new SearchStrategy { Goal = "best" }, // heuristic → rating
            Model("Unrated", 100m),
            Model("FourStar", 100m, rating: 4.0, ratingCount: 10),
            Model("FiveStar", 100m, rating: 5.0, ratingCount: 3)));

        CardOrder(cut, "Unrated", "FourStar", "FiveStar")
            .Should().ContainInOrder("FiveStar", "FourStar", "Unrated");
    }

    [Fact]
    public void NoStrategy_KeepsRelevanceOrder()
    {
        var cut = Render(Result(null, Model("First", 900m), Model("Second", 100m)));
        // relevance = incoming order, so the pricier "First" must stay first.
        CardOrder(cut, "First", "Second").Should().ContainInOrder("First", "Second");
    }

    [Fact]
    public void StreamingPush_SameSearch_DoesNotReseedSort_ButNewSearchDoes()
    {
        // A live search streams a NEW ProductSearchResult reference per push for the SAME search
        // (Query+Geo unchanged) — those pushes must not re-derive the sort. Only a genuinely new
        // SEARCH may reseed it.
        var models = new[] { Model("Expensive", 900m), Model("Cheap", 100m), Model("Middle", 400m) };

        var cut = Render(Result(new SearchStrategy { DefaultSort = "price_asc" }, "q", models));
        CardOrder(cut, "Expensive", "Cheap", "Middle")
            .Should().ContainInOrder("Cheap", "Middle", "Expensive");

        // Streaming push: new Result INSTANCE, same Query/Geo, but a strategy now claiming
        // price_desc. No reseed may happen — the grid must stay price-ascending.
        cut.SetParametersAndRender(p => p.Add(
            x => x.Result, Result(new SearchStrategy { DefaultSort = "price_desc" }, "q", models)));
        CardOrder(cut, "Expensive", "Cheap", "Middle")
            .Should().ContainInOrder("Cheap", "Middle", "Expensive");

        // A genuinely NEW search (different Query) with the same strategy DOES reseed: order flips.
        cut.SetParametersAndRender(p => p.Add(
            x => x.Result, Result(new SearchStrategy { DefaultSort = "price_desc" }, "q2", models)));
        CardOrder(cut, "Expensive", "Cheap", "Middle")
            .Should().ContainInOrder("Expensive", "Middle", "Cheap");
    }

    [Fact]
    public void FacetControl_RendersWithUnionOptions()
    {
        var strategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "size", Label = "Diaper Size", Values = new[] { "3" } } }
        };
        var cut = Render(Result(strategy, WithSpec("A", "Size", "4"), WithSpec("B", "size", "5")));

        cut.Markup.Should().Contain("Diaper Size"); // the facet control label renders
    }

    [Fact]
    public void FacetPreselection_FromQuerySpecs_FiltersTheGrid()
    {
        var strategy = new SearchStrategy
        {
            Specs = new Dictionary<string, string> { ["size"] = "4" }, // the user SAID size 4
            Facets = new[] { new SearchFacet { Key = "size", Label = "Size" } }
        };
        var cut = Render(Result(strategy,
            WithSpec("SizeFour", "Size", "4"),
            WithSpec("SizeFive", "Size", "5")));

        cut.Markup.Should().Contain("SizeFour");
        cut.Markup.Should().NotContain("SizeFive"); // filtered out by the pre-selected facet
    }

    [Fact]
    public void StreamingPush_SameSearch_KeepsFacetSelection()
    {
        // Same identity rule as the sort seed: streamed pushes for the SAME search refresh the
        // facet OPTIONS but must not reset the applied SELECTIONS; only a new search resets them.
        var strategy = new SearchStrategy
        {
            Specs = new Dictionary<string, string> { ["size"] = "4" },
            Facets = new[] { new SearchFacet { Key = "size", Label = "Size" } }
        };
        var cut = Render(Result(strategy, "q",
            WithSpec("SizeFour", "Size", "4"),
            WithSpec("SizeFive", "Size", "5")));
        cut.Markup.Should().Contain("SizeFour");
        cut.Markup.Should().NotContain("SizeFive"); // pre-selection applied

        // Streaming push: NEW Result instance, SAME Query/Geo, one more size-4 model landed.
        // The size=4 selection must survive and apply to the refreshed model set.
        cut.SetParametersAndRender(p => p.Add(x => x.Result, Result(strategy, "q",
            WithSpec("SizeFour", "Size", "4"),
            WithSpec("SizeFive", "Size", "5"),
            WithSpec("SizeFourB", "size", "4"))));
        cut.Markup.Should().Contain("SizeFour");
        cut.Markup.Should().Contain("SizeFourB");
        cut.Markup.Should().NotContain("SizeFive"); // selection retained across the push

        // A genuinely NEW search (different Query) whose strategy states NO specs resets the
        // selections back to Any — everything renders again.
        var noSpecsStrategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "size", Label = "Size" } }
        };
        cut.SetParametersAndRender(p => p.Add(x => x.Result, Result(noSpecsStrategy, "q2",
            WithSpec("SizeFour", "Size", "4"),
            WithSpec("SizeFive", "Size", "5"))));
        cut.Markup.Should().Contain("SizeFour");
        cut.Markup.Should().Contain("SizeFive"); // reset to Any on the new search
    }

    [Fact]
    public void NoStrategy_RendersNoFacetControls_AndAllModels()
    {
        var cut = Render(Result(null, Model("One", 100m), Model("Two", 200m)));
        cut.Markup.Should().Contain("One");
        cut.Markup.Should().Contain("Two");
        // Exactly the 4 generic selects (Brand/Source/Condition/Sort) — no facet selects appear.
        cut.FindComponents<MudSelect<string>>().Count.Should().Be(4);
    }

    [Fact]
    public void StockChip_RendersFromOfferAvailability_AndOnlyWhenKnown()
    {
        var inStock = Model("StockedFan", 100m) with
        {
            Offers = new[] { new PriceOffer { Source = "s", Price = 100m, Currency = "JOD", Availability = "متوفر" } }
        };
        var soldOut = Model("GoneFan", 120m) with
        {
            Offers = new[] { new PriceOffer { Source = "s", Price = 120m, Currency = "JOD", Availability = "sold out" } }
        };
        var silent = Model("QuietFan", 90m); // no availability anywhere → no chip

        var cut = Render(Result(null, inStock, soldOut, silent));

        cut.Markup.Should().Contain("In stock");
        cut.Markup.Should().Contain("Out of stock");
        // Exactly one of each chip: the silent card must not render a guessed state.
        System.Text.RegularExpressions.Regex.Matches(cut.Markup, "In stock").Count.Should().Be(1);
        System.Text.RegularExpressions.Regex.Matches(cut.Markup, "Out of stock").Count.Should().Be(1);
    }
}
