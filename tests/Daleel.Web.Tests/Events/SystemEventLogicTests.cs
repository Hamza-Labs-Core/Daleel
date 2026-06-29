using Daleel.Web.Events;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Events;

/// <summary>
/// The pure, DB-free logic behind the unified admin event timeline: re-bucketing the provider-shaped
/// pipeline firehose into the timeline's user-facing categories, and the filter/pagination predicates the
/// live Postgres store is a thin shell over. Covering these here gives the subsystem real coverage without
/// standing up a database — the same approach <see cref="EventStoreLogicTests"/> takes.
/// </summary>
public class SystemEventLogicTests
{
    // ── Projection: re-bucketing pipeline events into timeline categories ──────────────────────────

    [Theory]
    // The event type names the entity directly — it wins over the coarse provider category.
    [InlineData(EventCategory.Cache, "cache.hit", SystemEventCategory.Cache)]
    [InlineData(EventCategory.Profile, "profile.brand", SystemEventCategory.Brand)]
    [InlineData(EventCategory.Profile, "store.saved", SystemEventCategory.Store)]
    [InlineData(EventCategory.Extract, "item.deepdive", SystemEventCategory.Item)]
    [InlineData("pipeline", "subworkflow_failures", SystemEventCategory.Workflow)]
    // No entity in the type → fall back to the provider-shaped category.
    [InlineData(EventCategory.Llm, "chat", SystemEventCategory.Llm)]
    [InlineData(EventCategory.Places, "places/details", SystemEventCategory.Store)]
    [InlineData(EventCategory.Scrape, "scrape/markdown", SystemEventCategory.Item)]
    [InlineData(EventCategory.Search, "shopping", SystemEventCategory.Search)]
    public void CategoryOf_ReBucketsCorrectly(string pipelineCategory, string eventType, string expected)
    {
        SystemEventProjection.CategoryOf(pipelineCategory, eventType).Should().Be(expected);
    }

    [Fact]
    public void FromPipelineEvent_CarriesFieldsSeverityAndUserHash()
    {
        var pe = new PipelineEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Category = EventCategory.Scrape,
            EventType = "scrape/markdown",
            Provider = "Context.dev",
            SearchId = "42",
            Success = false,
            MetadataJson = "{\"url\":\"https://store.jo\"}"
        };

        var ev = SystemEventProjection.FromPipelineEvent(pe, userHash: "abc123");

        ev.Category.Should().Be(SystemEventCategory.Item);
        ev.EventType.Should().Be("scrape/markdown");
        ev.Severity.Should().Be(SystemEventSeverity.Error); // Success == false → error
        ev.CorrelationId.Should().Be("42");
        ev.UserHash.Should().Be("abc123");
        ev.Source.Should().Contain("Context.dev");
        ev.DetailsJson.Should().Contain("store.jo");
    }

    [Fact]
    public void FromPipelineEvent_SuccessIsInfo()
    {
        var pe = new PipelineEvent { Category = EventCategory.Llm, EventType = "chat", Success = true };
        SystemEventProjection.FromPipelineEvent(pe, null).Severity.Should().Be(SystemEventSeverity.Info);
    }

    // ── Filters: category / severity / free-text ──────────────────────────────────────────────────

    private static SystemEvent Ev(string category, string severity, string summary, string? type = null) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Category = category,
        Severity = severity,
        Summary = summary,
        EventType = type ?? summary,
        DetailsJson = "{}"
    };

    [Fact]
    public void Apply_NoFilters_ReturnsEverything()
    {
        var events = new[]
        {
            Ev(SystemEventCategory.Search, SystemEventSeverity.Info, "a"),
            Ev(SystemEventCategory.User, SystemEventSeverity.Error, "b")
        };

        SystemEventFilters.Apply(events, new SystemEventQuery()).Should().HaveCount(2);
    }

    [Fact]
    public void Apply_CategoryFilter_KeepsOnlyMatchingCategories()
    {
        var events = new[]
        {
            Ev(SystemEventCategory.Search, SystemEventSeverity.Info, "a"),
            Ev(SystemEventCategory.User, SystemEventSeverity.Info, "b"),
            Ev(SystemEventCategory.Cache, SystemEventSeverity.Info, "c")
        };

        var q = new SystemEventQuery { Categories = new[] { SystemEventCategory.Search, SystemEventCategory.Cache } };

        SystemEventFilters.Apply(events, q).Select(e => e.Summary).Should().BeEquivalentTo("a", "c");
    }

    [Fact]
    public void Apply_SeverityFilter_KeepsOnlyMatchingSeverities()
    {
        var events = new[]
        {
            Ev(SystemEventCategory.Search, SystemEventSeverity.Info, "a"),
            Ev(SystemEventCategory.Search, SystemEventSeverity.Error, "b")
        };

        var q = new SystemEventQuery { Severities = new[] { SystemEventSeverity.Error } };

        SystemEventFilters.Apply(events, q).Select(e => e.Summary).Should().ContainSingle().Which.Should().Be("b");
    }

    [Fact]
    public void Apply_TextFilter_IsCaseInsensitiveAcrossFields()
    {
        var events = new[]
        {
            Ev(SystemEventCategory.Search, SystemEventSeverity.Info, "Search completed: Samsung TV"),
            Ev(SystemEventCategory.User, SystemEventSeverity.Info, "User signed in"),
            new SystemEvent
            {
                Category = SystemEventCategory.Item, Severity = SystemEventSeverity.Error,
                Summary = "scrape failed", EventType = "item.deepdive",
                DetailsJson = "{\"error\":\"TIMEOUT contacting store.jo\"}"
            }
        };

        // Matches the summary (case-insensitively)…
        SystemEventFilters.Apply(events, new SystemEventQuery { Text = "samsung" })
            .Should().ContainSingle().Which.Summary.Should().Contain("Samsung");

        // …and the details payload.
        SystemEventFilters.Apply(events, new SystemEventQuery { Text = "timeout" })
            .Should().ContainSingle().Which.EventType.Should().Be("item.deepdive");
    }

    // ── Pagination ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Page_ReturnsRequestedSlice()
    {
        var events = Enumerable.Range(1, 25)
            .Select(i => Ev(SystemEventCategory.Search, SystemEventSeverity.Info, $"e{i}"))
            .ToList();

        var page2 = SystemEventFilters.Page(events, new SystemEventQuery { Page = 2, PageSize = 10 });

        page2.Should().HaveCount(10);
        page2[0].Summary.Should().Be("e11");
    }

    [Fact]
    public void Page_ClampsPageSizeAndFloorsPage()
    {
        var events = Enumerable.Range(1, 10)
            .Select(i => Ev(SystemEventCategory.Search, SystemEventSeverity.Info, $"e{i}"))
            .ToList();

        // Page 0 is floored to 1; an over-large page size still works (clamped to 500, well above 10).
        SystemEventFilters.Page(events, new SystemEventQuery { Page = 0, PageSize = 9999 })
            .Should().HaveCount(10);
    }
}
