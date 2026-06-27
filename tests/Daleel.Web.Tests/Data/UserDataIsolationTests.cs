using Daleel.Web.Data;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// The security-critical tests: prove that one user can never read, fetch, or delete another's
/// rows through the repositories. If any of these regress, data isolation is broken.
/// </summary>
public class UserDataIsolationTests
{
    private const string Alice = "alice-id";
    private const string Bob = "bob-id";

    private static SearchHistoryEntry Entry(string userId, string query) => new()
    {
        UserId = userId,
        Query = query,
        QueryType = "ask",
        Geo = "jordan",
        Model = "test/model",
        ResultSummary = "summary",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static SavedResult Saved(string userId, string title) => new()
    {
        UserId = userId,
        Title = title,
        ResultType = "ask",
        ResultJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task History_List_ReturnsOnlyOwnRows()
    {
        using var ctx = new PostgresTestContext();
        var repo = new SearchHistoryRepository(ctx.Db);
        await repo.AddAsync(Entry(Alice, "alice question"));
        await repo.AddAsync(Entry(Bob, "bob question"));

        var alice = await repo.ListAsync(Alice);
        var bob = await repo.ListAsync(Bob);

        alice.Items.Should().ContainSingle().Which.Query.Should().Be("alice question");
        bob.Items.Should().ContainSingle().Which.Query.Should().Be("bob question");
    }

    [Fact]
    public async Task History_Get_DeniesCrossUserAccess()
    {
        using var ctx = new PostgresTestContext();
        var repo = new SearchHistoryRepository(ctx.Db);
        var aliceRow = await repo.AddAsync(Entry(Alice, "secret"));

        // Bob asks for Alice's row by id — must come back empty.
        (await repo.GetAsync(Bob, aliceRow.Id)).Should().BeNull();
        (await repo.GetAsync(Alice, aliceRow.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task History_Delete_CannotDeleteAnotherUsersRow()
    {
        using var ctx = new PostgresTestContext();
        var repo = new SearchHistoryRepository(ctx.Db);
        var aliceRow = await repo.AddAsync(Entry(Alice, "keep me"));

        var deleted = await repo.DeleteAsync(Bob, aliceRow.Id);

        deleted.Should().BeFalse();
        (await repo.GetAsync(Alice, aliceRow.Id)).Should().NotBeNull("Bob's delete must not touch Alice's row");
    }

    [Fact]
    public async Task History_Clear_OnlyClearsOwnRows()
    {
        using var ctx = new PostgresTestContext();
        var repo = new SearchHistoryRepository(ctx.Db);
        await repo.AddAsync(Entry(Alice, "a1"));
        await repo.AddAsync(Entry(Alice, "a2"));
        await repo.AddAsync(Entry(Bob, "b1"));

        var cleared = await repo.ClearAsync(Alice);

        cleared.Should().Be(2);
        (await repo.ListAsync(Alice)).TotalCount.Should().Be(0);
        (await repo.ListAsync(Bob)).TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Saved_List_ReturnsOnlyOwnRows()
    {
        using var ctx = new PostgresTestContext();
        var repo = new SavedResultRepository(ctx.Db);
        await repo.AddAsync(Saved(Alice, "alice report"));
        await repo.AddAsync(Saved(Bob, "bob report"));

        (await repo.ListAsync(Alice)).Should().ContainSingle().Which.Title.Should().Be("alice report");
        (await repo.ListAsync(Bob)).Should().ContainSingle().Which.Title.Should().Be("bob report");
    }

    [Fact]
    public async Task Saved_GetAndDelete_DenyCrossUserAccess()
    {
        using var ctx = new PostgresTestContext();
        var repo = new SavedResultRepository(ctx.Db);
        var aliceRow = await repo.AddAsync(Saved(Alice, "private"));

        (await repo.GetAsync(Bob, aliceRow.Id)).Should().BeNull();
        (await repo.DeleteAsync(Bob, aliceRow.Id)).Should().BeFalse();
        (await repo.GetAsync(Alice, aliceRow.Id)).Should().NotBeNull();
    }
}
