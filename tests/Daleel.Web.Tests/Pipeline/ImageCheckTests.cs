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
    public async Task Strips_the_haram_image_and_keeps_the_item()
    {
        var (h, ctx, item, store) = Build(new FakeClassifier(HaramImg));

        await h.ExecuteAsync(item, ctx, default);

        var models = store.Current.Products!.Models;
        models.Should().HaveCount(2, "an image flag strips the picture, never removes the product");
        models.Single(m => m.Name == "Dress A").ImageUrl.Should().BeNull("the haram image is stripped");
        models.Single(m => m.Name == "Dress B").ImageUrl.Should().Be(CleanImg, "a clean image is untouched");
    }

    [Fact]
    public async Task No_vision_model_is_a_no_op()
    {
        var (h, ctx, item, store) = Build(new NullHalalImageClassifier());

        var outcome = await h.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        store.Current.Products!.Models.Single(m => m.Name == "Dress A").ImageUrl.Should().Be(HaramImg);
    }

    private sealed class FakeClassifier : IHalalImageClassifier
    {
        private readonly string _haramUrl;
        public FakeClassifier(string haramUrl) => _haramUrl = haramUrl;
        public bool IsConfigured => true;
        public Task<IReadOnlyList<ImageVerdict>> ClassifyAsync(IReadOnlyList<string> imageUrls, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ImageVerdict>>(imageUrls.Contains(_haramUrl)
                ? new[] { new ImageVerdict(_haramUrl, true, "immodest", 0.95, "revealing") }
                : Array.Empty<ImageVerdict>());
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
