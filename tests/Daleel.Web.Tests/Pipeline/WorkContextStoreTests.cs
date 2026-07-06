using System.Text.Json;
using Daleel.Web.Data;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// WorkContextStore semantics against real Postgres: the findings ledger is a bounded append-only
/// tail, synthesis upserts on the unique (job, scope, key) index and records a high-water mark, and
/// the TTL prune drops old rows. These are the durability guarantees the synthesis unit relies on.
/// </summary>
public class WorkContextStoreTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly WorkContextStore _store;

    public WorkContextStoreTests()
    {
        var connStr = PostgresTestServer.CreateFreshDatabase();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DaleelDbContext>(o => o.UseNpgsql(connStr));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DaleelDbContext>().Database.EnsureCreated();
        }

        _store = new WorkContextStore(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    private DaleelDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<DaleelDbContext>();

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Append_creates_the_row_and_accumulates_findings()
    {
        await _store.AppendFindingAsync(1, WorkContextScope.Product, "p_abc", "itemdive", "specs filled: 5");
        await _store.AppendFindingAsync(1, WorkContextScope.Product, "p_abc", "verifypage", "page condition=new");

        var row = await _store.GetAsync(1, WorkContextScope.Product, "p_abc");
        row.Should().NotBeNull();
        CountFindings(row!.FindingsJson).Should().Be(2, "both findings appended to the one upserted row");
        row.Synthesis.Should().BeNull("no synthesis yet");
    }

    [Fact]
    public async Task Ledger_is_capped_at_the_last_forty()
    {
        for (var i = 0; i < 50; i++)
        {
            await _store.AppendFindingAsync(1, WorkContextScope.Search, "", "step", $"note {i}");
        }

        var row = await _store.GetAsync(1, WorkContextScope.Search, "");
        CountFindings(row!.FindingsJson).Should().Be(40, "the ledger keeps only the most recent 40 — a bounded tail");

        await using var db = NewDb();
        (await db.WorkContexts.CountAsync(w => w.SearchJobId == 1)).Should().Be(1, "one row, not fifty");
    }

    [Fact]
    public async Task SetSynthesis_records_text_high_water_mark_and_version()
    {
        await _store.AppendFindingAsync(1, WorkContextScope.Brand, "delonghi", "brandresearch", "sites=2");
        await _store.SetSynthesisAsync(1, WorkContextScope.Brand, "delonghi", "A solid mid-tier brand.", foldedCount: 1);

        var row = await _store.GetAsync(1, WorkContextScope.Brand, "delonghi");
        row!.Synthesis.Should().Be("A solid mid-tier brand.");
        row.SynthesizedFindingCount.Should().Be(1, "the folded count is the idempotency high-water mark");
        row.SynthesisVersion.Should().Be(1);
        row.SynthesizedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Synthesis_before_any_finding_still_upserts_one_row()
    {
        // The search scope is often synthesized without its own findings appended first.
        await _store.SetSynthesisAsync(2, WorkContextScope.Search, "", "Market overview.", foldedCount: 0);

        var row = await _store.GetAsync(2, WorkContextScope.Search, "");
        row.Should().NotBeNull();
        row!.Synthesis.Should().Be("Market overview.");
        row.FindingsJson.Should().Be("[]");
    }

    [Fact]
    public async Task Prune_removes_only_rows_older_than_the_cutoff()
    {
        await _store.AppendFindingAsync(3, WorkContextScope.Search, "", "step", "note");

        (await _store.PruneAsync(DateTimeOffset.UtcNow.AddDays(-1))).Should().Be(0, "the fresh row is newer than the cutoff");
        (await _store.PruneAsync(DateTimeOffset.UtcNow.AddMinutes(1))).Should().Be(1, "now it's older than the cutoff");

        (await _store.GetAsync(3, WorkContextScope.Search, "")).Should().BeNull();
    }

    [Fact]
    public async Task List_for_job_returns_every_scope()
    {
        await _store.AppendFindingAsync(4, WorkContextScope.Search, "", "plan", "3 products");
        await _store.AppendFindingAsync(4, WorkContextScope.Product, "p_x", "itemdive", "specs");
        await _store.AppendFindingAsync(4, WorkContextScope.Brand, "beko", "brandresearch", "sites=1");

        var all = await _store.ListForJobAsync(4);
        all.Should().HaveCount(3);
        all.Select(w => w.Scope).Should().BeEquivalentTo(
            new[] { WorkContextScope.Search, WorkContextScope.Product, WorkContextScope.Brand });
    }

    private static int CountFindings(string json) =>
        JsonDocument.Parse(json).RootElement.GetArrayLength();
}
