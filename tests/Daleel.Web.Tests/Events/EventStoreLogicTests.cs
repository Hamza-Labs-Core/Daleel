using Daleel.Core.Observability;
using Daleel.Web.Events;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Events;

/// <summary>
/// The pure, DB-free logic behind the Postgres event store: mapping the existing ApiCall telemetry
/// into categorized events, rolling a flat event list up into the dashboard's per-provider/-category
/// usage report, and parsing a DATABASE_URL into an Npgsql connection string. The live Postgres
/// store is a thin shell over these, so covering them here gives the subsystem real test coverage
/// without standing up a database.
/// </summary>
public class EventStoreLogicTests
{
    [Theory]
    [InlineData("SerpAPI", "shopping", EventCategory.Search)]
    [InlineData("Bing", "web", EventCategory.Search)]
    [InlineData("Google Places", "places/details", EventCategory.Places)]
    [InlineData("Context.dev", "scrape/markdown", EventCategory.Scrape)]
    [InlineData("Context.dev", "extract", EventCategory.Extract)]
    [InlineData("OpenRouter/openai", "chat", EventCategory.Llm)]
    [InlineData("Cloudflare", "render", EventCategory.Scrape)]
    public void CategoryOf_BucketsProvidersCorrectly(string provider, string endpoint, string expected)
    {
        PipelineEventFactory.CategoryOf(provider, endpoint).Should().Be(expected);
    }

    [Fact]
    public void FromApiCall_CarriesCostStatusAndCorrelationId()
    {
        var call = new ApiCall
        {
            Timestamp = DateTimeOffset.UtcNow,
            Provider = "Context.dev",
            Endpoint = "scrape/markdown",
            RequestSummary = "https://store.jo",
            ResponseTimeMs = 120,
            Status = ApiCallStatus.Error,
            EstimatedCost = 0.001m
        };

        var ev = PipelineEventFactory.FromApiCall(call, searchId: "42");

        ev.Category.Should().Be(EventCategory.Scrape);
        ev.SearchId.Should().Be("42");
        ev.EstimatedCost.Should().Be(0.001m);
        ev.Success.Should().BeFalse();
        ev.MetadataJson.Should().Contain("store.jo");
    }

    [Fact]
    public void UsageReport_Build_RollsUpPerProviderAndCategory_CostSorted()
    {
        var events = new List<PipelineEvent>
        {
            Ev(EventCategory.Search, "SerpAPI", 0.01m, ms: 100, ok: true),
            Ev(EventCategory.Search, "SerpAPI", 0.01m, ms: 300, ok: false),
            Ev(EventCategory.Llm, "OpenRouter", 0.05m, ms: 800, ok: true),
            Ev(EventCategory.Scrape, "Context.dev", 0.001m, ms: 50, ok: true),
        };

        var report = UsageReport.Build(events);

        report.TotalEvents.Should().Be(4);
        report.TotalCost.Should().Be(0.071m);

        // Providers sorted by cost: OpenRouter (0.05) > SerpAPI (0.02) > Context.dev (0.001).
        report.Providers.Select(p => p.Provider).Should().ContainInOrder("OpenRouter", "SerpAPI", "Context.dev");
        var serp = report.Providers.Single(p => p.Provider == "SerpAPI");
        serp.Count.Should().Be(2);
        serp.Cost.Should().Be(0.02m);
        serp.Errors.Should().Be(1);
        serp.AvgMs.Should().Be(200);

        report.Categories.Single(c => c.Category == EventCategory.Llm).Cost.Should().Be(0.05m);
    }

    [Fact]
    public void UsageReport_Build_EmptyList_IsEmpty()
    {
        UsageReport.Build(Array.Empty<PipelineEvent>()).Should().BeSameAs(UsageReport.Empty);
    }

    [Fact]
    public void PostgresConnection_FromUrl_ConvertsDatabaseUrlToNpgsqlKeywords()
    {
        var conn = PostgresConnection.FromUrl("postgres://user:p%40ss@db.host:6543/daleel_events?sslmode=require");

        conn.Should().Contain("Host=db.host");
        conn.Should().Contain("Port=6543");
        conn.Should().Contain("Database=daleel_events");
        conn.Should().Contain("Username=user");
        conn.Should().Contain("Password=p@ss"); // URL-decoded
        conn.Should().Contain("SSL Mode=Require");
    }

    [Fact]
    public void PostgresConnection_FromUrl_DefaultsPortAndSsl()
    {
        var conn = PostgresConnection.FromUrl("postgresql://u:pw@localhost/db");

        conn.Should().Contain("Port=5432");
        conn.Should().Contain("SSL Mode=Require");
    }

    private static PipelineEvent Ev(string category, string provider, decimal cost, long ms, bool ok) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Category = category,
        Provider = provider,
        EventType = "x",
        EstimatedCost = cost,
        DurationMs = ms,
        Success = ok
    };
}
