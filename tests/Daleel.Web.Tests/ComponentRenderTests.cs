using Bunit;
using Daleel.Core.Models;
using Daleel.Web.Components.Shared;
using Daleel.Web.Pipeline;
using Daleel.Web.Translation;
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
        // Shared components also translate dynamic text through ITranslationService. Register a disabled
        // (pass-through) instance so they render the original text — the translation logic itself is
        // covered by TranslationServiceTests, not these render tests.
        Services.AddSingleton<ITranslationService>(new DisabledTranslationService());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>A no-op translator: Enabled=false, so every component falls through to the original text.</summary>
    private sealed class DisabledTranslationService : ITranslationService
    {
        public bool Enabled => false;
        public Task<string> TranslateAsync(string text, string targetLang, CancellationToken ct = default) =>
            Task.FromResult(text);
        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default) =>
            Task.FromResult(texts);
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

    // ── SearchProgress: the 8→6 phase collapse (redesign in commit 3168b1c). These render the REAL
    //    component (not a mockup) and lock in every claim the commit made: six phases not eight, the
    //    retired vault/stores steps gone, BuildingProfiles+FindingStores sharing one "details" phase,
    //    and the six-phase percent math. Enum values live in SearchProgressSignal (Analyzing=0 … Done=7).
    private IRenderedComponent<SearchProgress> RenderProgress(string[] messages, bool active) =>
        RenderComponent<SearchProgress>(p => p
            .Add(x => x.Messages, messages)
            .Add(x => x.Active, active));

    private static string Sig(SearchStep step, string key, params object[] args) =>
        SearchProgressSignal.Encode(step, key, args);

    [Fact]
    public void SearchProgress_CollapsesEightWireStagesToSixPhases()
    {
        var cut = RenderProgress(new[] { Sig(SearchStep.Analyzing, "Progress.Msg.Analyzing") }, active: true);

        cut.FindAll(".daleel-step-v").Count.Should().Be(6);          // six phases, not the old eight
        cut.Markup.Should().Contain("Understanding your search");    // new vague label resolves from resx
        cut.Markup.Should().NotContain("Checking vault");            // retired step is gone from the view
        cut.Markup.Should().NotContain("Finding stores");
    }

    [Fact]
    public void SearchProgress_ProfilesAndStoresShareOneDetailsPhase_LatestWins()
    {
        // BuildingProfiles(4) and FindingStores(5) both map to display phase index 3 ("details").
        var msgs = new[]
        {
            Sig(SearchStep.BuildingProfiles, "Progress.Msg.BuildingProfiles", 3, 2),
            Sig(SearchStep.FindingStores, "Progress.Msg.VerifyingStore", "Smart Buy"),
        };

        var cut = RenderProgress(msgs, active: true);

        // Collapse proof: two wire stages produce ONE detail slot (phase 3) — not two separate lines.
        cut.FindAll(".daleel-step-detail").Count.Should().Be(1);
        cut.Markup.Should().Contain("67%");                          // phase 3 → step 4 of 6 → 67%
        cut.Markup.Should().Contain("Checking out Smart Buy");       // latest signal wins the shared slot
        cut.Markup.Should().NotContain("Getting the story behind");  // the earlier one was overwritten
    }

    [Fact]
    public void SearchProgress_DoneReachesFinalPhaseAndSettlesAllGreen()
    {
        var cut = RenderProgress(new[] { Sig(SearchStep.Done, "Progress.Msg.Done") }, active: false);

        cut.Markup.Should().Contain("100%");                         // Done → phase 6 of 6
        cut.FindAll(".daleel-step-v.done").Count.Should().Be(6);     // settled view = all six done
    }

    [Fact]
    public void SearchProgress_RetiredVaultStageFoldsIntoUnderstanding()
    {
        // A CheckingVault(1) signal must not surface a vault row — it folds into phase 0 ("understanding").
        var cut = RenderProgress(new[] { Sig(SearchStep.CheckingVault, "Progress.Msg.Analyzing") }, active: true);

        cut.FindAll(".daleel-step-v").Count.Should().Be(6);
        cut.Markup.Should().Contain("17%");                          // phase 0 → step 1 of 6 → 17%
        cut.Markup.Should().NotContain("Checking vault");
    }
}
