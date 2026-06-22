using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>Auto-save, pagination, search, and save/delete/list behaviour for the repositories.</summary>
public class RepositoryTests
{
    private const string User = "user-1";

    [Fact]
    public async Task History_AutoSave_PersistsAndIsListedNewestFirst()
    {
        using var ctx = new SqliteTestContext();
        var repo = new SearchHistoryRepository(ctx.Db);

        await repo.AddAsync(new SearchHistoryEntry
        {
            UserId = User, Query = "older", QueryType = "ask", Geo = "jordan",
            Model = "m", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await repo.AddAsync(new SearchHistoryEntry
        {
            UserId = User, Query = "newer", QueryType = "brand", Geo = "jordan",
            Model = "m", CreatedAt = DateTimeOffset.UtcNow
        });

        var page = await repo.ListAsync(User);

        page.TotalCount.Should().Be(2);
        page.Items[0].Query.Should().Be("newer", "newest entries come first");
    }

    [Fact]
    public async Task History_List_FiltersBySearchText()
    {
        using var ctx = new SqliteTestContext();
        var repo = new SearchHistoryRepository(ctx.Db);
        await repo.AddAsync(Make("best AC in Amman"));
        await repo.AddAsync(Make("cheapest fridge"));

        var page = await repo.ListAsync(User, search: "fridge");

        page.TotalCount.Should().Be(1);
        page.Items.Single().Query.Should().Be("cheapest fridge");
    }

    [Fact]
    public async Task History_List_Paginates()
    {
        using var ctx = new SqliteTestContext();
        var repo = new SearchHistoryRepository(ctx.Db);
        for (var i = 0; i < 25; i++)
        {
            await repo.AddAsync(Make($"query {i:00}", DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var page1 = await repo.ListAsync(User, page: 1, pageSize: 10);
        var page3 = await repo.ListAsync(User, page: 3, pageSize: 10);

        page1.TotalCount.Should().Be(25);
        page1.Items.Should().HaveCount(10);
        page1.TotalPages.Should().Be(3);
        page3.Items.Should().HaveCount(5);
    }

    [Fact]
    public async Task Saved_SaveListDelete_RoundTrips()
    {
        using var ctx = new SqliteTestContext();
        var repo = new SavedResultRepository(ctx.Db);

        var saved = await repo.AddAsync(new SavedResult
        {
            UserId = User, Title = "My report", ResultType = "brand",
            ResultJson = "{\"Brand\":\"Zain\"}", Notes = "interesting",
            CreatedAt = DateTimeOffset.UtcNow
        });

        saved.Id.Should().BeGreaterThan(0);

        var fetched = await repo.GetAsync(User, saved.Id);
        fetched.Should().NotBeNull();
        fetched!.ResultJson.Should().Contain("Zain");

        (await repo.ListAsync(User)).Should().ContainSingle();

        (await repo.DeleteAsync(User, saved.Id)).Should().BeTrue();
        (await repo.ListAsync(User)).Should().BeEmpty();
    }

    private static SearchHistoryEntry Make(string query, DateTimeOffset? at = null) => new()
    {
        UserId = User,
        Query = query,
        QueryType = "ask",
        Geo = "jordan",
        Model = "m",
        CreatedAt = at ?? DateTimeOffset.UtcNow
    };
}
