using Daleel.Agent;
using Daleel.Core.Models;
using Daleel.Core.Moderation;
using Daleel.Web.Data;
using Daleel.Web.Moderation;
using Daleel.Web.Pipeline;
using Daleel.Web.Pipeline.Enrichment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// The clean-shot re-scrape: an item whose only photo the product-shot screen rejected (a promo banner,
/// a logo, a collage) has its own detail page re-read for a real gallery shot, instead of showing a
/// placeholder forever. ImageLookup can never do this — those items HAVE an ImageUrl, so it skips them.
/// </summary>
public class CleanShotTests
{
    private const string BannerImg = "https://store.jo/banner-50-off.jpg";
    private const string HaramImg = "https://store.jo/indecent.jpg";
    private const string GalleryImg = "https://store.jo/product/ac-12000.jpg";

    // ── The trigger: ImageCheck is the only step that knows a photo was REJECTED ──────────────

    [Fact]
    public async Task ImageCheck_QueuesACleanShotRescrape_ForAnItemTheProductScreenLeftImageless()
    {
        var (h, ctx, item, _, queue) = BuildCheck(
            new ProductModel { Name = "Gree AC 12000", ImageUrl = BannerImg },
            rejectAsNonProductShot: BannerImg);

        await h.ExecuteAsync(item, ctx, default);

        var queued = queue.Enqueued.Should().ContainSingle(i => i.Kind == EnrichmentUnit.CleanShot).Subject;
        EnrichmentWorkQueue.ReadPayload<CleanShotPayload>(queued.Payload)!.Names
            .Should().Equal(new[] { "Gree AC 12000" });
    }

    [Fact]
    public async Task ImageCheck_DoesNotQueueACleanShot_WhenTheItemStillHasSomethingToRender()
    {
        // The banner was rejected but a real photo survived — the card renders, so there is nothing to fix.
        var (h, ctx, item, _, queue) = BuildCheck(
            new ProductModel { Name = "Gree AC 12000", ImageUrl = BannerImg, Images = new[] { GalleryImg } },
            rejectAsNonProductShot: BannerImg);

        await h.ExecuteAsync(item, ctx, default);

        queue.Enqueued.Should().NotContain(i => i.Kind == EnrichmentUnit.CleanShot);
    }

    [Fact]
    public async Task ImageCheck_DoesNotQueueACleanShot_ForAnItemHiddenByTheHalalScreen()
    {
        // Hunting for another photo of a product whose imagery was judged haram is a moderation decision
        // this unit has no business making — the re-scrape is scoped to product-shot rejections only.
        var (h, ctx, item, _, queue) = BuildCheck(
            new ProductModel { Name = "Dress A", ImageUrl = HaramImg },
            rejectAsNonProductShot: BannerImg, // present, but not this item's photo
            haramUrl: HaramImg);

        await h.ExecuteAsync(item, ctx, default);

        queue.Enqueued.Should().NotContain(i => i.Kind == EnrichmentUnit.CleanShot);
    }

    [Fact]
    public async Task ImageCheck_QueuesTheCleanShotOnlyOncePerJob()
    {
        // The re-scrape enqueues a fresh screen when its photos land; without the latch that screen would
        // re-trigger the re-scrape on an item whose new photos are ALSO unclean, and the two would loop.
        var (h, ctx, item, _, queue) = BuildCheck(
            new ProductModel { Name = "Gree AC 12000", ImageUrl = BannerImg },
            rejectAsNonProductShot: BannerImg);
        queue.AlreadyQueuedKinds.Add(EnrichmentUnit.CleanShot);

        await h.ExecuteAsync(item, ctx, default);

        queue.Enqueued.Should().NotContain(i => i.Kind == EnrichmentUnit.CleanShot);
    }

    // ── The handler: re-read the detail page, append, re-screen ───────────────────────────────

    [Fact]
    public async Task CleanShot_AppendsTheDetailPagePhoto_AndQueuesAScreenForIt()
    {
        var model = new ProductModel { Name = "Gree AC 12000", ImageUrl = BannerImg };
        var (h, ctx, item, store, queue) = BuildCleanShot(model, found: new[] { GalleryImg }, names: "Gree AC 12000");

        var outcome = await h.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        var patched = store.Current.Products!.Models.Single();
        patched.Images.Should().Contain(GalleryImg, "the detail page's real photo is added");
        patched.ImageUrl.Should().Be(BannerImg,
            "the rejected photo stays a candidate — hiding is via VerifiedImages, so a whitelist can un-hide it");
        patched.CandidateImages.Should().Equal(new[] { BannerImg, GalleryImg });
        queue.Enqueued.Should().ContainSingle(i => i.Kind == EnrichmentUnit.ImageCheck,
            "the grid is fail-closed: an unscreened photo is an invisible photo");
    }

    [Fact]
    public async Task CleanShot_SkipsAnItemThatAlreadyHasSomethingToRender()
    {
        // A later screen (or an admin whitelist) may have given the item a photo between the trigger and
        // this unit running — then there is nothing to re-scrape and no page fetch to pay for.
        var model = new ProductModel
        {
            Name = "Gree AC 12000", ImageUrl = GalleryImg, VerifiedImages = new[] { GalleryImg }
        };
        var (h, ctx, item, _, queue) = BuildCleanShot(model, found: new[] { "https://x/other.jpg" }, names: "Gree AC 12000");

        await h.ExecuteAsync(item, ctx, default);

        queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanShot_AddsNothing_WhenTheDetailPageYieldsNoNewPhoto()
    {
        var model = new ProductModel { Name = "Gree AC 12000", ImageUrl = BannerImg };
        var (h, ctx, item, store, queue) = BuildCleanShot(model, found: Array.Empty<string>(), names: "Gree AC 12000");

        await h.ExecuteAsync(item, ctx, default);

        store.Current.Products!.Models.Single().Images.Should().BeEmpty();
        queue.Enqueued.Should().BeEmpty("nothing landed, so there is nothing to screen");
    }

    // ── Builders ─────────────────────────────────────────────────────────────────────────────

    private static (ImageCheckHandler H, EnrichmentUnitContext Ctx, EnrichmentWorkItem Item, RecordingStore Store, FakeQueue Queue)
        BuildCheck(ProductModel model, string rejectAsNonProductShot, string? haramUrl = null)
    {
        var store = new RecordingStore(Answer(model));
        var services = new ServiceCollection();
        services.AddSingleton<IHalalImageClassifier>(new FakeClassifier(haramUrl));
        services.AddSingleton<IProductImageScreen>(new FakeProductScreen(rejectAsNonProductShot));
        var queue = new FakeQueue();

        var ctx = new EnrichmentUnitContext
        {
            Services = services.BuildServiceProvider(),
            Job = new SearchJob { Id = 1, Query = "air conditioner", Geo = "jordan" },
            Agent = () => null!,
            Results = store,
            Queue = queue
        };
        var item = new EnrichmentWorkItem { Id = 1, SearchJobId = 1, Kind = EnrichmentUnit.ImageCheck };
        return (new ImageCheckHandler(NullLogger<ImageCheckHandler>.Instance), ctx, item, store, queue);
    }

    private static (CleanShotHandler H, EnrichmentUnitContext Ctx, EnrichmentWorkItem Item, RecordingStore Store, FakeQueue Queue)
        BuildCleanShot(ProductModel model, IReadOnlyList<string> found, params string[] names)
    {
        var store = new RecordingStore(Answer(model));
        var services = new ServiceCollection();
        services.AddSingleton<IItemEnrichmentService>(new FakeImageFinder(found));
        var queue = new FakeQueue();

        var ctx = new EnrichmentUnitContext
        {
            Services = services.BuildServiceProvider(),
            Job = new SearchJob { Id = 1, Query = "air conditioner", Geo = "jordan" },
            Agent = () => null!,
            Results = store,
            Queue = queue
        };
        var item = new EnrichmentWorkItem
        {
            Id = 2,
            SearchJobId = 1,
            Kind = EnrichmentUnit.CleanShot,
            Payload = EnrichmentWorkQueue.Payload(new CleanShotPayload(names.ToList()))
        };
        return (new CleanShotHandler(), ctx, item, store, queue);
    }

    private static AgentAnswer Answer(ProductModel model) => new()
    {
        Question = "air conditioner",
        Geo = "jordan",
        Products = new ProductSearchResult { Models = new[] { model }, Brands = Array.Empty<BrandInfo>() }
    };

    private sealed class FakeProductScreen(string reject) : IProductImageScreen
    {
        public bool IsConfigured => true;
        public Task<IReadOnlySet<string>> RejectNonProductShotsAsync(
            IReadOnlyList<string> imageUrls, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(
                imageUrls.Where(u => u == reject).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class FakeClassifier(string? haramUrl) : IHalalImageClassifier
    {
        public bool IsConfigured => true;
        public Task<ImageClassifierResult> ClassifyAsync(
            IReadOnlyList<string> imageUrls, CancellationToken ct = default, bool bypassCache = false) =>
            Task.FromResult(new ImageClassifierResult(
                haramUrl is not null && imageUrls.Contains(haramUrl)
                    ? new[] { new ImageVerdict(haramUrl, true, "immodest", 0.95, "revealing") }
                    : Array.Empty<ImageVerdict>(),
                Array.Empty<string>()));
    }

    private sealed class FakeQueue : IEnrichmentWorkQueue
    {
        public List<EnrichmentWorkItem> Enqueued { get; } = new();
        public HashSet<string> AlreadyQueuedKinds { get; } = new(StringComparer.Ordinal);

        public Task EnqueueAsync(IReadOnlyList<EnrichmentWorkItem> items, CancellationToken ct = default)
        {
            Enqueued.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<bool> AnyOfKindAsync(int searchJobId, string kind, CancellationToken ct = default) =>
            Task.FromResult(AlreadyQueuedKinds.Contains(kind) || Enqueued.Any(i => i.Kind == kind));

        public Task<bool> EnqueueFanOutAsync(
            int searchJobId, string selfKind, IReadOnlyList<EnrichmentWorkItem> children, CancellationToken ct = default)
        {
            Enqueued.AddRange(children);
            return Task.FromResult(children.Count > 0);
        }

        public Task<IReadOnlyList<EnrichmentWorkItem>> ClaimAsync(int max, TimeSpan lease, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EnrichmentWorkItem>>(Array.Empty<EnrichmentWorkItem>());
        public Task CompleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RetryAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequeueAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(long id, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> OpenCountAsync(int searchJobId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ReapExhaustedAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class RecordingStore(AgentAnswer a) : IEnrichedResultStore
    {
        public AgentAnswer Current { get; private set; } = a;
        public Task<AgentAnswer?> LoadAsync(int jobId, CancellationToken ct = default) =>
            Task.FromResult<AgentAnswer?>(Current);
        public Task<bool> PatchAsync(
            EnrichmentWorkItem item, Func<AgentAnswer, AgentAnswer?> mutate, CancellationToken ct = default)
        {
            if (mutate(Current) is { } patched)
            {
                Current = patched;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    /// <summary>Only <see cref="FindImageForItemAsync"/> matters here; the rest of the surface is inert.</summary>
    private sealed class FakeImageFinder(IReadOnlyList<string> found) : IItemEnrichmentService
    {
        public Task<IReadOnlyList<string>> FindImageForItemAsync(
            AgentService agent, ProductModel item, CancellationToken ct) => Task.FromResult(found);

        public Task<ItemEnrichmentResult> EnrichAsync(
            AgentService agent, ProductSearchResult products, Action<string> progress, string? searchId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<List<ProductModel>?> FillFromBrandDatabaseUnitAsync(List<ProductModel> models, string? geo, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<List<ProductModel>?> IdentifyViaVisionUnitAsync(List<ProductModel> models, string? geo, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ProductModel?> DeepDiveItemAsync(AgentService agent, ProductModel item, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created, CatalogGate Gate)> AttachCatalogForDomainAsync(
            AgentService agent, List<ProductModel> models, string domain, string? storeName, string? geo, string? searchId,
            string? query, string? entryUrl, CancellationToken ct, bool skipVendorCatalog = false) =>
            throw new NotSupportedException();
        public Task<(List<ProductModel>? Models, int Priced, IReadOnlyList<string> Created)> AttachScrapedPricesAsync(
            List<ProductModel> models, string domain, string? storeName, string? query, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<List<ProductModel>?> BackfillConditionsUnitAsync(List<ProductModel> models, CancellationToken ct) =>
            throw new NotSupportedException();
        public IReadOnlyList<(string Domain, string? StoreName, string? EntryUrl)> SelectCatalogDomains(ProductSearchResult products) =>
            throw new NotSupportedException();
        public IReadOnlyList<string> SelectBrandsForHarvest(ProductSearchResult products) =>
            throw new NotSupportedException();
    }
}
