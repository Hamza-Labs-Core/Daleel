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
        new() { Query = "q", Geo = "jordan", Country = "Jordan", Models = models, Strategy = strategy };

    // MudSelect (the sort/filter dropdowns) resolves popovers through MudPopoverProvider at render
    // time — render it alongside the grid, same as MudBlazor's own component-test setup requires.
    private IRenderedComponent<ProductListings> Render(ProductSearchResult result)
    {
        RenderComponent<MudPopoverProvider>();
        return RenderComponent<ProductListings>(p => p.Add(x => x.Result, result));
    }

    private static IReadOnlyList<string> CardOrder(IRenderedComponent<ProductListings> cut, params string[] names)
        => names.OrderBy(n => cut.Markup.IndexOf(n, StringComparison.Ordinal)).ToList();

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
}
