using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Identification;
using Daleel.Web.Profiles;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Identification;

/// <summary>
/// Drives the identification cascade end-to-end over real Postgres repositories, faking only the two external
/// edges (cross-region discovery and the vision LLM). Pins the cheapest-first order: text → discovery →
/// vision, plus the memoization that stops the same image pair being compared twice.
/// </summary>
public class SmartProductIdentifierTests
{
    private static async Task<(Brand Brand, SmartProductIdentifier Id, FakeCatalogSearcher Searcher, FakeVisionMatcher Vision, VisionMatchCacheRepository Cache, BrandModelRepository Models)>
        BuildAsync(PostgresTestContext ctx, FakeCatalogSearcher searcher, FakeVisionMatcher vision)
    {
        var brands = new BrandRepository(ctx.Db);
        var models = new BrandModelRepository(ctx.Db);
        var cache = new VisionMatchCacheRepository(ctx.Db);
        var brand = await brands.UpsertAsync(new Brand
        {
            Name = "Samsung", Website = "samsung.com", LastRefreshed = DateTimeOffset.UtcNow
        });

        var identifier = new SmartProductIdentifier(
            brands, models, searcher, vision, cache, new ProfileOptions(),
            NullLogger<SmartProductIdentifier>.Instance);
        return (brand, identifier, searcher, vision, cache, models);
    }

    private static ProductModel Listing(string? model, string name = "TV", string? image = null) =>
        new() { Brand = "Samsung", Model = model, Name = name, ImageUrl = image };

    [Fact]
    public async Task Identify_ReturnsNone_WhenBrandUnknown()
    {
        using var ctx = new PostgresTestContext();
        var (_, id, _, _, _, _) = await BuildAsync(ctx, new FakeCatalogSearcher(), new FakeVisionMatcher(VisionMatchResult.NoMatch));

        var result = await id.IdentifyAsync(new ProductModel { Brand = "Nokia", Model = "X" });

        result.Matched.Should().BeFalse();
    }

    [Fact]
    public async Task Identify_TextMatchesKnownModel_WithoutDiscoveryOrVision()
    {
        using var ctx = new PostgresTestContext();
        var searcher = new FakeCatalogSearcher();
        var vision = new FakeVisionMatcher(new VisionMatchResult(true, 1, "x"));
        var (brand, id, _, _, _, models) = await BuildAsync(ctx, searcher, vision);
        await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24", Category = "Smartphone", LastRefreshed = DateTimeOffset.UtcNow
        });

        var result = await id.IdentifyAsync(Listing("Galaxy S24"));

        result.Method.Should().Be("text");
        result.Confidence.Should().Be(1.0);
        result.CanonicalModelName.Should().Be("Galaxy S24");
        result.Category.Should().Be("Smartphone");
        searcher.Calls.Should().Be(0, "a known text match short-circuits before discovery");
        vision.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Identify_TextMatchesByRegionalAlias()
    {
        using var ctx = new PostgresTestContext();
        var (brand, id, _, _, _, models) = await BuildAsync(ctx, new FakeCatalogSearcher(), new FakeVisionMatcher(VisionMatchResult.NoMatch));
        await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "Galaxy S24",
            RegionalAliases = new List<string> { "SM-S921B/JO" }, LastRefreshed = DateTimeOffset.UtcNow
        });

        var result = await id.IdentifyAsync(Listing("SM-S921B/JO"));

        result.Matched.Should().BeTrue();
        result.CanonicalModelName.Should().Be("Galaxy S24");
    }

    [Fact]
    public async Task Identify_DiscoversThenTextMatches_OnInitialMiss()
    {
        using var ctx = new PostgresTestContext();
        var discovered = new BrandModel
        {
            Id = 42, ModelName = "Galaxy S24", ModelKey = BrandModel.Normalize("Galaxy S24"), Category = "Smartphone"
        };
        var searcher = new FakeCatalogSearcher(discovered);
        var (_, id, _, vision, _, _) = await BuildAsync(ctx, searcher, new FakeVisionMatcher(VisionMatchResult.NoMatch));

        var result = await id.IdentifyAsync(Listing("Galaxy S24"));

        result.Method.Should().Be("text");
        searcher.Calls.Should().Be(1, "the initial DB miss triggers cross-region discovery");
    }

    [Fact]
    public async Task Identify_FallsBackToVision_AndCachesTheVerdict()
    {
        using var ctx = new PostgresTestContext();
        var searcher = new FakeCatalogSearcher();
        var vision = new FakeVisionMatcher(new VisionMatchResult(true, 0.93, "Galaxy S24"));
        var (brand, id, _, _, cache, models) = await BuildAsync(ctx, searcher, vision);

        // A persisted model with an image but a non-matching name → only vision can connect them.
        var model = await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "SM-Mystery", ImageUrl = "https://brand/img.jpg",
            LastRefreshed = DateTimeOffset.UtcNow
        });
        searcher.Return(model);

        var result = await id.IdentifyAsync(Listing("Vague TV Model", image: "https://store.jo/p.jpg"));

        result.Method.Should().Be("vision");
        result.BrandModelId.Should().Be(model.Id);
        result.Confidence.Should().Be(0.93);
        vision.Calls.Should().Be(1);
        (await cache.CountAsync()).Should().Be(1, "the verdict is memoized for next time");
    }

    [Fact]
    public async Task Identify_UsesCachedVision_WithoutCallingTheModelAgain()
    {
        using var ctx = new PostgresTestContext();
        var searcher = new FakeCatalogSearcher();
        var vision = new FakeVisionMatcher(new VisionMatchResult(true, 1, "should-not-be-called"));
        var (brand, id, _, _, cache, models) = await BuildAsync(ctx, searcher, vision);

        var model = await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "SM-Mystery", ImageUrl = "https://brand/img.jpg",
            LastRefreshed = DateTimeOffset.UtcNow
        });
        searcher.Return(model);

        const string storeImage = "https://store.jo/p.jpg";
        await cache.UpsertAsync(new VisionMatchCache
        {
            StoreImageHash = SmartProductIdentifier.HashImage(storeImage),
            BrandModelId = model.Id, Confidence = 0.95, MatchedModelName = "Galaxy S24",
            MatchedAt = DateTimeOffset.UtcNow
        });

        var result = await id.IdentifyAsync(Listing("Vague", image: storeImage));

        result.Method.Should().Be("vision");
        result.Confidence.Should().Be(0.95);
        vision.Calls.Should().Be(0, "the cached verdict means the vision model is never re-called");
    }

    [Fact]
    public async Task Identify_ReturnsNone_WhenVisionBelowThreshold()
    {
        using var ctx = new PostgresTestContext();
        var searcher = new FakeCatalogSearcher();
        var vision = new FakeVisionMatcher(new VisionMatchResult(false, 0.2, null));
        var (brand, id, _, _, _, models) = await BuildAsync(ctx, searcher, vision);

        var model = await models.UpsertAsync(new BrandModel
        {
            BrandId = brand.Id, ModelName = "SM-Mystery", ImageUrl = "https://brand/img.jpg",
            LastRefreshed = DateTimeOffset.UtcNow
        });
        searcher.Return(model);

        var result = await id.IdentifyAsync(Listing("Vague", image: "https://store.jo/p.jpg"));

        result.Matched.Should().BeFalse();
    }
}

internal sealed class FakeCatalogSearcher : IBrandCatalogSearcher
{
    private List<BrandModel> _models;
    public int Calls { get; private set; }

    public FakeCatalogSearcher(params BrandModel[] models) => _models = models.ToList();

    public void Return(params BrandModel[] models) => _models = models.ToList();

    public Task<IReadOnlyList<BrandModel>> DiscoverAsync(Brand brand, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<BrandModel>>(_models);
    }
}

internal sealed class FakeVisionMatcher : IVisionMatcher
{
    private readonly VisionMatchResult _result;
    public int Calls { get; private set; }
    public bool IsConfigured { get; }

    public FakeVisionMatcher(VisionMatchResult result, bool configured = true)
    {
        _result = result;
        IsConfigured = configured;
    }

    public Task<VisionMatchResult> CompareAsync(
        string storeImageUrl, string brandImageUrl, string? candidateModelName, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(_result);
    }
}
