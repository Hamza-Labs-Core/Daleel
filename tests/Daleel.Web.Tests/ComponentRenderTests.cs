using Bunit;
using Daleel.Core.Models;
using Daleel.Web.Components.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// bUnit render tests for the presentational shared components. MudBlazor services are
/// registered and JS interop runs in loose mode (MudBlazor probes the DOM on render).
/// </summary>
public class ComponentRenderTests : TestContext
{
    public ComponentRenderTests()
    {
        Services.AddMudServices();
        // Shared components now resolve display strings through IStringLocalizer<SharedResource>;
        // register localization so they can be constructed in the test container (default culture
        // → the English .resx values embedded in the Daleel.Web assembly).
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void ReportView_RendersParagraphsAndTitle()
    {
        var cut = RenderComponent<ReportView>(p => p
            .Add(x => x.Title, "Summary")
            .Add(x => x.Text, "First paragraph.\n\nSecond paragraph."));

        cut.Markup.Should().Contain("Summary");
        cut.Markup.Should().Contain("First paragraph.");
        cut.Markup.Should().Contain("Second paragraph.");
    }

    [Fact]
    public void ReportView_SetsRtlForArabicText()
    {
        var cut = RenderComponent<ReportView>(p => p
            .Add(x => x.Text, "هذا تقرير عن السوق الأردني."));

        cut.Markup.Should().Contain("dir=\"rtl\"");
    }

    [Fact]
    public void ReportView_RendersNothingForEmptyText()
    {
        var cut = RenderComponent<ReportView>(p => p.Add(x => x.Text, ""));
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void ProgressLog_ShowsLinesAndCompletionState()
    {
        var cut = RenderComponent<ProgressLog>(p => p
            .Add(x => x.Busy, false)
            .Add(x => x.Lines, new[] { "planning research", "gathering results" }));

        cut.Markup.Should().Contain("planning research");
        cut.Markup.Should().Contain("gathering results");
    }

    [Fact]
    public void SentimentView_RendersCountsWhenMentionsExist()
    {
        var summary = new SentimentSummary { PositiveCount = 5, NeutralCount = 2, NegativeCount = 1 };

        var cut = RenderComponent<SentimentView>(p => p.Add(x => x.Summary, summary));

        cut.Markup.Should().Contain("5 positive");
        cut.Markup.Should().Contain("1 negative");
    }

    [Fact]
    public void SentimentView_RendersNothingWithoutMentions()
    {
        var cut = RenderComponent<SentimentView>(p => p.Add(x => x.Summary, new SentimentSummary()));
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void StoreCard_RendersNameRatingAndContact()
    {
        var store = new StoreLocation
        {
            Name = "Smart Electronics",
            Address = "Mecca St, Amman",
            Phone = "+962 6 555 1234",
            Rating = 4.6,
            ReviewCount = 220,
            Website = "https://example.com"
        };

        var cut = RenderComponent<StoreCard>(p => p.Add(x => x.Store, store));

        cut.Markup.Should().Contain("Smart Electronics");
        cut.Markup.Should().Contain("Mecca St, Amman");
        cut.Markup.Should().Contain("4.6");
    }
}
