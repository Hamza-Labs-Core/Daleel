using Daleel.Agent;
using Daleel.Core.Models;
using Daleel.Core.Moderation;
using Daleel.Web.Data;
using Daleel.Web.Pipeline.Enrichment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// The halal image-safety pass strips a haram GRID image (keeping the item), leaves clean images and
/// unrelated products alone, and no-ops when no vision model is configured. This closes the gap where
/// enrichment-assigned product images bypassed the gather-stage vision moderation.
/// </summary>
public class ImageCheckTests
{
    private const string HaramImg = "https://x/indecent.jpg";
    private const string CleanImg = "https://x/coffee.jpg";

    private static (ImageCheckHandler H, EnrichmentUnitContext Ctx, EnrichmentWorkItem Item, RecordingStore Store)
        Build(IHalalImageClassifier classifier)
    {
        var answer = new AgentAnswer
        {
            Question = "women dress", Geo = "jordan",
            Products = new ProductSearchResult
            {
                Models = new[]
                {
                    new ProductModel { Name = "Dress A", ImageUrl = HaramImg },
                    new ProductModel { Name = "Dress B", ImageUrl = CleanImg }
                },
                Brands = Array.Empty<BrandInfo>()
            }
        };
        var store = new RecordingStore(answer);
        var services = new ServiceCollection();
        services.AddSingleton(classifier);
        var provider = services.BuildServiceProvider();

        var ctx = new EnrichmentUnitContext
        {
            Services = provider,
            Job = new SearchJob { Id = 1, Query = "women dress", Geo = "jordan" },
            Agent = () => null!,
            Results = store,
            Queue = null!
        };
        var item = new EnrichmentWorkItem { Id = 1, SearchJobId = 1, Kind = EnrichmentUnit.ImageCheck };
        return (new ImageCheckHandler(NullLogger<ImageCheckHandler>.Instance), ctx, item, store);
    }

    [Fact]
    public async Task Verifies_clean_images_and_hides_haram_ones_keeping_the_item()
    {
        var (h, ctx, item, store) = Build(new FakeClassifier(HaramImg));

        var outcome = await h.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        var models = store.Current.Products!.Models;
        models.Should().HaveCount(2, "an image flag hides the picture, never removes the product");
        var dressA = models.Single(m => m.Name == "Dress A");
        dressA.DisplayImageUrl.Should().BeNull("the haram image is hidden (never verified)");
        dressA.ImageUrl.Should().Be(HaramImg, "the raw URL is preserved so an admin whitelist / a retry can un-hide it");
        models.Single(m => m.Name == "Dress B").DisplayImageUrl.Should().Be(CleanImg, "a clean image is verified and shown");
    }

    [Fact]
    public async Task No_vision_model_shows_all_images()
    {
        // No key = moderation intentionally off (a deployment choice) → show images as-is, NOT fail-closed
        // hide; otherwise a missing key would blank the whole app.
        var (h, ctx, item, store) = Build(new NullHalalImageClassifier());

        var outcome = await h.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        store.Current.Products!.Models.Single(m => m.Name == "Dress A").DisplayImageUrl.Should().Be(HaramImg);
        store.Current.Products!.Models.Single(m => m.Name == "Dress B").DisplayImageUrl.Should().Be(CleanImg);
    }

    [Fact]
    public async Task Screens_the_whole_gallery_and_shows_only_the_verified_photos()
    {
        // Each item now carries ALL photos from its listings; the screen verifies each and the item shows
        // only the clean ones — a flagged photo in the gallery is hidden while the clean ones still render.
        const string cleanA = "https://x/a-clean.jpg";
        const string haramB = "https://x/b-haram.jpg";
        const string cleanC = "https://x/c-clean.jpg";
        var answer = new AgentAnswer
        {
            Question = "women dress", Geo = "jordan",
            Products = new ProductSearchResult
            {
                Models = new[] { new ProductModel { Name = "Dress", Images = new[] { cleanA, haramB, cleanC } } },
                Brands = Array.Empty<BrandInfo>()
            }
        };
        var store = new RecordingStore(answer);
        var services = new ServiceCollection();
        services.AddSingleton<IHalalImageClassifier>(new FakeClassifier(haramB));
        var ctx = new EnrichmentUnitContext
        {
            Services = services.BuildServiceProvider(),
            Job = new SearchJob { Id = 1, Query = "women dress", Geo = "jordan" },
            Agent = () => null!, Results = store, Queue = null!
        };
        var item = new EnrichmentWorkItem { Id = 1, SearchJobId = 1, Kind = EnrichmentUnit.ImageCheck };

        await new ImageCheckHandler(NullLogger<ImageCheckHandler>.Instance).ExecuteAsync(item, ctx, default);

        var model = store.Current.Products!.Models.Single();
        model.DisplayImages.Should().Equal(new[] { cleanA, cleanC }, "the two clean photos show; the flagged one is hidden");
        model.Images.Should().Contain(haramB, "the raw gallery is preserved so a whitelist/retry can un-hide it");
    }

    [Fact]
    public async Task Unscreened_image_stays_hidden_and_the_unit_requeues()
    {
        // The vision screen could not run (e.g. OpenRouter HTTP 402 out-of-credits): the image must NOT be
        // shown, and the unit must STAY QUEUED so it re-screens once billing/infra recovers — never fail-open.
        var (h, ctx, item, store) = Build(new FakeClassifier(unscreened: new[] { HaramImg, CleanImg }));

        var outcome = await h.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Requeue>("could-not-screen keeps the unit queued, no attempt consumed");
        var models = store.Current.Products!.Models;
        models.Single(m => m.Name == "Dress A").DisplayImageUrl.Should().BeNull("could-not-screen ⇒ hidden");
        models.Single(m => m.Name == "Dress B").DisplayImageUrl.Should().BeNull("could-not-screen ⇒ hidden");
    }

    private sealed class FakeClassifier : IHalalImageClassifier
    {
        private readonly string? _haramUrl;
        private readonly IReadOnlyCollection<string>? _unscreened;
        public FakeClassifier(string? haramUrl = null, IReadOnlyCollection<string>? unscreened = null)
            => (_haramUrl, _unscreened) = (haramUrl, unscreened);
        public bool IsConfigured => true;
        public Task<ImageClassifierResult> ClassifyAsync(
            IReadOnlyList<string> imageUrls, CancellationToken ct = default, bool bypassCache = false)
        {
            var flagged = _haramUrl is not null && imageUrls.Contains(_haramUrl)
                ? new[] { new ImageVerdict(_haramUrl, true, "immodest", 0.95, "revealing") }
                : Array.Empty<ImageVerdict>();
            var unscreened = _unscreened is null
                ? Array.Empty<string>()
                : imageUrls.Where(u => _unscreened.Contains(u)).ToArray();
            return Task.FromResult(new ImageClassifierResult(flagged, unscreened));
        }
    }

    private sealed class RecordingStore : IEnrichedResultStore
    {
        public AgentAnswer Current { get; private set; }
        public RecordingStore(AgentAnswer a) => Current = a;
        public Task<AgentAnswer?> LoadAsync(int jobId, CancellationToken ct = default) => Task.FromResult<AgentAnswer?>(Current);
        public Task<bool> PatchAsync(EnrichmentWorkItem item, Func<AgentAnswer, AgentAnswer?> mutate, CancellationToken ct = default)
        {
            if (mutate(Current) is { } patched) { Current = patched; return Task.FromResult(true); }
            return Task.FromResult(false);
        }
    }
}
