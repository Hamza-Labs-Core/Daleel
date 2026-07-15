using Bunit;
using Daleel.Core.Models;
using Daleel.Web.Components.Shared;
using Daleel.Web.Translation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// The per-card review signal: renders nothing without data, and shows rating + count + the
/// buyer/social/store reviews (with sentiment) when present. Same bUnit setup as the other
/// component render tests.
/// </summary>
public class ReviewSignalTests : TestContext
{
    public ReviewSignalTests()
    {
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddSingleton<ITranslationService>(new NoTranslation());
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

    [Fact]
    public void NoReviewData_RendersNothing()
    {
        var cut = RenderComponent<ReviewSignal>(p => p
            .Add(x => x.Model, new ProductModel { Name = "bare" }));
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Rating_AndBuyerReview_Render()
    {
        var model = new ProductModel
        {
            Name = "m",
            Rating = 4.5,
            RatingCount = 12,
            RatedReviews = new[] { new ProductReview("Loved it", 5, "Aisha") }
        };
        var cut = RenderComponent<ReviewSignal>(p => p.Add(x => x.Model, model));

        cut.Markup.Should().Contain("4.5");
        cut.Markup.Should().Contain("Loved it");
    }

    [Fact]
    public void SocialQuote_FromBrandReputation_Renders()
    {
        var model = new ProductModel
        {
            Name = "m",
            BrandReputation = new BrandReputation
            {
                Social = new SocialProof
                {
                    Reviews = new[] { new UserReview { Quote = "الجودة ممتازة", Sentiment = Sentiment.Positive } }
                }
            }
        };
        var cut = RenderComponent<ReviewSignal>(p => p.Add(x => x.Model, model));
        cut.Markup.Should().Contain("الجودة ممتازة");
    }

    [Fact]
    public void StoreReviews_PassedIn_Render()
    {
        var cut = RenderComponent<ReviewSignal>(p => p
            .Add(x => x.Model, new ProductModel { Name = "m" })
            .Add(x => x.StoreReviews, new[] { new StoreReview { Text = "fast delivery" } }));
        cut.Markup.Should().Contain("fast delivery");
    }

    [Fact]
    public void Header_Click_TogglesCollapse()
    {
        var model = new ProductModel
        {
            Name = "m",
            Rating = 4.5,
            RatedReviews = new[] { new ProductReview("Loved it", 5, "Aisha") }
        };
        var cut = RenderComponent<ReviewSignal>(p => p.Add(x => x.Model, model));

        // Verified markup behavior: MudCollapse always renders its container (children stay in the
        // DOM), and expanding adds the "mud-collapse-entering" transition class to it.
        cut.Find(".mud-collapse-container").ClassList.Should().NotContain("mud-collapse-entering");

        cut.Find("div[title]").Click();

        cut.Find(".mud-collapse-container").ClassList.Should().Contain("mud-collapse-entering");
    }
}
