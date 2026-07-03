using Daleel.Web.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// Batch feedback operations on the admin "Filtered content" log: bulk rating, bulk
/// whitelist/undo, bulk delete (which must take linked whitelist entries with it), and the
/// legacy keyless purge. Runs against real PostgreSQL so the set-based deletes translate.
/// </summary>
public sealed class FilteredContentLogBatchTests : IDisposable
{
    private readonly PostgresTestContext _ctx = new();

    private FilteredContentLogRepository Repo() => new(_ctx.Db);

    private async Task<long[]> SeedAsync(params FilteredContentLog[] rows)
    {
        _ctx.Db.FilteredContentLogs.AddRange(rows);
        await _ctx.Db.SaveChangesAsync();
        return rows.Select(r => r.Id).ToArray();
    }

    private static FilteredContentLog Finding(string category = "alcohol", string? hash = "h1",
        string? sourceUrl = null, string? imageUrl = null) => new()
    {
        Category = category,
        Rule = "beer",
        Kind = "SearchResult",
        Content = "Imported Beer 6-pack",
        ContentHash = hash,
        SourceUrl = sourceUrl,
        ImageUrl = imageUrl,
        DecisionSource = "llm",
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task RateMany_SetsRatingAndTimestamp_OnAllRows()
    {
        var ids = await SeedAsync(Finding(hash: "a"), Finding(hash: "b"), Finding(hash: "c"));

        var done = await Repo().RateManyAsync(ids, 1);

        done.Should().Be(3);
        var rows = await _ctx.Db.FilteredContentLogs.AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(r => r.Rating == 1 && r.RatedAt != null);
    }

    [Fact]
    public async Task RateMany_WithNull_ClearsRatings()
    {
        var ids = await SeedAsync(Finding(hash: "a"), Finding(hash: "b"));
        await Repo().RateManyAsync(ids, -1);

        await Repo().RateManyAsync(ids, null);

        var rows = await _ctx.Db.FilteredContentLogs.AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(r => r.Rating == null && r.RatedAt == null);
    }

    [Fact]
    public async Task WhitelistMany_CreatesEntries_AndSkipsKeylessRows()
    {
        var ids = await SeedAsync(
            Finding(hash: "a"),
            Finding(hash: "b"),
            Finding(hash: null)); // legacy keyless — must be skipped, not fault the batch

        var done = await Repo().WhitelistManyAsync(ids);

        done.Should().Be(2);
        (await _ctx.Db.ModerationWhitelist.CountAsync()).Should().Be(2);
        var keyless = await _ctx.Db.FilteredContentLogs.AsNoTracking()
            .SingleAsync(r => r.ContentHash == null);
        keyless.WhitelistEntryId.Should().BeNull();
    }

    [Fact]
    public async Task RemoveWhitelistMany_UndoesOnlyWhitelistedRows()
    {
        var ids = await SeedAsync(Finding(hash: "a"), Finding(hash: "b"), Finding(hash: "c"));
        await Repo().WhitelistManyAsync(new[] { ids[0], ids[1] });

        var done = await Repo().RemoveWhitelistManyAsync(ids);

        done.Should().Be(2);
        (await _ctx.Db.ModerationWhitelist.CountAsync()).Should().Be(0);
        var rows = await _ctx.Db.FilteredContentLogs.AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(r => r.WhitelistEntryId == null);
    }

    [Fact]
    public async Task DeleteMany_RemovesRows_AndTheirWhitelistEntries()
    {
        var ids = await SeedAsync(Finding(hash: "a"), Finding(hash: "b"), Finding(hash: "c"));
        await Repo().WhitelistManyAsync(new[] { ids[0] });

        var done = await Repo().DeleteManyAsync(new[] { ids[0], ids[1] });

        done.Should().Be(2);
        (await _ctx.Db.FilteredContentLogs.CountAsync()).Should().Be(1);
        // The deleted finding's whitelist entry must go with it — no orphaned bypass.
        (await _ctx.Db.ModerationWhitelist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PurgeKeyless_DeletesOnlyLegacyRows()
    {
        await SeedAsync(
            Finding(hash: null),                                  // legacy — purged
            Finding(hash: null),                                  // legacy — purged
            Finding(hash: "a"),                                   // keyed — kept
            Finding(hash: null, sourceUrl: "https://x.example"),  // has a URL key — kept
            Finding(hash: null, imageUrl: "https://img.example/i.jpg")); // image key — kept

        var repo = Repo();
        (await repo.CountKeylessAsync()).Should().Be(2);

        var done = await repo.PurgeKeylessAsync();

        done.Should().Be(2);
        (await _ctx.Db.FilteredContentLogs.CountAsync()).Should().Be(3);
        (await repo.CountKeylessAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BatchMethods_AreNoOps_OnEmptyOrUnknownIds()
    {
        var repo = Repo();
        (await repo.RateManyAsync(Array.Empty<long>(), 1)).Should().Be(0);
        (await repo.DeleteManyAsync(Array.Empty<long>())).Should().Be(0);
        (await repo.WhitelistManyAsync(new long[] { 12345 })).Should().Be(0);
        (await repo.RemoveWhitelistManyAsync(new long[] { 12345 })).Should().Be(0);
    }

    public void Dispose() => _ctx.Dispose();
}
