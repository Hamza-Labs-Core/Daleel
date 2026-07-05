using Daleel.Web.Data;
using Daleel.Web.Events;
using Daleel.Web.Pipeline.Enrichment;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

public class QueueDashboardServiceTests : IDisposable
{
    private readonly ServiceProvider _provider;

    public QueueDashboardServiceTests()
    {
        var connStr = PostgresTestServer.CreateFreshDatabase(); // once — the AddDbContext lambda runs per scope
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DaleelDbContext>(o => o.UseNpgsql(connStr));
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DaleelDbContext>().Database.EnsureCreated();
    }

    private QueueDashboardService Service(DaleelDbContext db) => new(db, new NullSystemEventLog());

    private static EnrichmentWorkItem Item(
        string kind, string status, int attempts = 1, string? error = null,
        DateTimeOffset? created = null, DateTimeOffset? completed = null) => new()
    {
        SearchJobId = 1, UserId = "u1", HistoryEntryId = 1, ResultType = "products",
        Kind = kind, Payload = "{}", Status = status, Attempts = attempts, LastError = error,
        CreatedAt = created ?? DateTimeOffset.UtcNow, CompletedAt = completed,
        NotBefore = created ?? DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Snapshot_counts_statuses_kinds_recovered_and_dead_ledger()
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        db.EnrichmentWorkItems.AddRange(
            Item(EnrichmentUnit.ItemDive, WorkItemStatus.Done, completed: now),
            Item(EnrichmentUnit.ItemDive, WorkItemStatus.Done, attempts: 3, completed: now), // recovered
            Item(EnrichmentUnit.CatalogAttach, WorkItemStatus.Pending, created: now.AddMinutes(-10)),
            Item(EnrichmentUnit.CatalogAttach, WorkItemStatus.Running),
            Item(EnrichmentUnit.CatalogAttach, WorkItemStatus.Dead, attempts: 4,
                error: "awaiting edge drain for x.jo", completed: now));
        await db.SaveChangesAsync();

        var d = await Service(db).SnapshotAsync(TimeSpan.FromHours(24));

        d.Pending.Should().Be(1);
        d.Running.Should().Be(1);
        d.Done.Should().Be(2);
        d.Dead.Should().Be(1);
        d.Recovered.Should().Be(1, "one done unit needed more than one attempt");
        d.OldestPendingAge.Should().NotBeNull();
        d.OldestPendingAge!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));

        var catalog = d.Kinds.Single(k => k.Kind == EnrichmentUnit.CatalogAttach);
        catalog.Pending.Should().Be(1);
        catalog.Dead.Should().Be(1);
        d.RecentDead.Should().ContainSingle()
            .Which.LastError.Should().Be("awaiting edge drain for x.jo");
        d.Drain.Should().BeNull("the null event log must read as 'not configured', never zeros");
    }

    [Fact]
    public async Task Old_terminal_rows_fall_out_of_the_window_but_live_states_never_do()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-3);
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DaleelDbContext>();
        db.EnrichmentWorkItems.AddRange(
            Item(EnrichmentUnit.ItemDive, WorkItemStatus.Done, created: old, completed: old),
            Item(EnrichmentUnit.ItemDive, WorkItemStatus.Pending, created: old)); // stuck — must show
        await db.SaveChangesAsync();

        var d = await Service(db).SnapshotAsync(TimeSpan.FromHours(24));

        d.Done.Should().Be(0, "terminal rows are windowed (throughput view)");
        d.Pending.Should().Be(1, "a stuck pending row is exactly what the dashboard exists to surface");
        d.OldestPendingAge!.Value.Should().BeGreaterThan(TimeSpan.FromDays(2));
    }

    public void Dispose() => _provider.Dispose();
}
