using System.Text.Json;
using Daleel.Core.Models;
using Daleel.Core.Persistence;
using Daleel.Web.Api;
using Daleel.Web.Data;
using Daleel.Web.Persistence;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Api;

/// <summary>
/// Endpoint smoke tests for the /api/v1 read surface, exercising the handlers directly (the same
/// static methods the routes map) over real PostgreSQL: the items list serves LIVE entities only
/// (alias rows excluded), the item document endpoint follows MergedIntoId aliases to the survivor,
/// and unknown ids 404.
/// </summary>
public class ApiV1EndpointTests
{
    /// <summary>Runs an IResult the way the pipeline would and captures status + parsed JSON body.</summary>
    private static async Task<(int Status, JsonDocument Body)> ExecuteAsync(IResult result)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;

        await result.ExecuteAsync(http);

        buffer.Position = 0;
        var body = buffer.Length > 0
            ? await JsonDocument.ParseAsync(buffer)
            : JsonDocument.Parse("{}");
        return (http.Response.StatusCode, body);
    }

    private static EntityRecord Record(string id, string name, string? mergedIntoId = null) => new()
    {
        Id = id,
        Intent = nameof(SearchIntentType.Product),
        Name = name,
        NameKey = EntityRecord.Normalize(name),
        Geo = "jordan",
        MergedIntoId = mergedIntoId,
        LastRefreshed = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task ListItems_ReturnsLiveEntitiesOnly_AliasRowsExcluded()
    {
        using var ctx = new PostgresTestContext();
        var repo = new EntityRecordRepository(ctx.Db);
        await repo.UpsertAsync(Record("p_live0001", "Samsung WW90 washing machine"));
        await repo.UpsertAsync(Record("p_alias001", "Samsung WW-90 (duplicate)", mergedIntoId: "p_live0001"));

        var (status, body) = await ExecuteAsync(await ApiV1Endpoints.ListItemsAsync(repo));

        status.Should().Be(200);
        var ids = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();
        ids.Should().Contain("p_live0001");
        ids.Should().NotContain("p_alias001");
    }

    [Fact]
    public async Task ListItems_FiltersByStore()
    {
        using var ctx = new PostgresTestContext();
        var store = await new StoreRepository(ctx.Db).UpsertAsync(new Store
        {
            Name = "Leaders Center",
            LastRefreshed = DateTimeOffset.UtcNow
        });
        var repo = new EntityRecordRepository(ctx.Db);
        var inStore = Record("p_instore01", "iPhone 15");
        inStore.StoreId = store.Id;
        await repo.UpsertAsync(inStore);
        await repo.UpsertAsync(Record("p_elsewhere", "Galaxy S24"));

        var (_, body) = await ExecuteAsync(await ApiV1Endpoints.ListItemsAsync(repo, store: store.Id));

        var ids = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();
        ids.Should().BeEquivalentTo("p_instore01");
    }

    [Fact]
    public async Task GetItem_FollowsAliasToTheSurvivorDocument()
    {
        using var ctx = new PostgresTestContext();
        var repo = new EntityRecordRepository(ctx.Db);
        await repo.UpsertAsync(Record("p_live0001", "Samsung WW90"));
        await repo.UpsertAsync(Record("p_alias001", "Samsung WW-90 (duplicate)", mergedIntoId: "p_live0001"));
        var store = new StubEntityStore("p_live0001", new EntityDocument
        {
            Id = "p_live0001",
            Name = "Samsung WW90",
            Intent = SearchIntentType.Product
        });

        // Ask for the OLD (merged-away) id: the endpoint must resolve the survivor's document.
        var (status, body) = await ExecuteAsync(await ApiV1Endpoints.GetItemAsync("p_alias001", repo, store));

        status.Should().Be(200);
        body.RootElement.GetProperty("id").GetString().Should().Be("p_live0001");
        body.RootElement.GetProperty("name").GetString().Should().Be("Samsung WW90");
    }

    [Fact]
    public async Task GetItem_UnknownId_Is404()
    {
        using var ctx = new PostgresTestContext();
        var repo = new EntityRecordRepository(ctx.Db);

        var (status, body) = await ExecuteAsync(
            await ApiV1Endpoints.GetItemAsync("p_missing", repo, new StubEntityStore(null, null)));

        status.Should().Be(404);
        body.RootElement.GetProperty("error").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task GetStore_And_GetBrand_RoundTripProfiles()
    {
        using var ctx = new PostgresTestContext();
        var store = await new StoreRepository(ctx.Db).UpsertAsync(new Store
        {
            Name = "Leaders Center",
            Location = "Amman",
            Website = "https://leaders.jo",
            LastRefreshed = DateTimeOffset.UtcNow
        });
        var brand = await new BrandRepository(ctx.Db).UpsertAsync(new Daleel.Web.Data.Brand
        {
            Name = "Samsung",
            CountryOfOrigin = "South Korea",
            LastRefreshed = DateTimeOffset.UtcNow
        });

        var (storeStatus, storeBody) = await ExecuteAsync(
            await ApiV1Endpoints.GetStoreAsync(store.Id, new StoreRepository(ctx.NewContext())));
        var (brandStatus, brandBody) = await ExecuteAsync(
            await ApiV1Endpoints.GetBrandAsync(brand.Id, new BrandRepository(ctx.NewContext())));

        storeStatus.Should().Be(200);
        storeBody.RootElement.GetProperty("name").GetString().Should().Be("Leaders Center");
        brandStatus.Should().Be(200);
        brandBody.RootElement.GetProperty("countryOfOrigin").GetString().Should().Be("South Korea");

        var (missingStatus, _) = await ExecuteAsync(
            await ApiV1Endpoints.GetStoreAsync(999_999, new StoreRepository(ctx.NewContext())));
        missingStatus.Should().Be(404);
    }

    /// <summary>Serves one canned R2 document for one id — the endpoint under test never needs real R2.</summary>
    private sealed class StubEntityStore : ISearchEntityStore
    {
        private readonly string? _id;
        private readonly EntityDocument? _document;

        public StubEntityStore(string? id, EntityDocument? document)
        {
            _id = id;
            _document = document;
        }

        public Task<EntityRecord?> SaveAsync(EntityDocument document, CancellationToken ct = default) =>
            Task.FromResult<EntityRecord?>(null);

        public Task<int> SaveAllAsync(IEnumerable<EntityDocument> documents, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<EntityDocument?> GetAsync(string id, SearchIntentType intent, CancellationToken ct = default) =>
            Task.FromResult(id == _id ? _document : null);
    }
}
