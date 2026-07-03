using Daleel.Core.Moderation;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Moderation;

public class HalalModeratorTests
{
    private sealed record Item(string Title, string? Snippet = null, string? Url = null, string? Image = null);

    private static readonly ModerationProjection<Item> Projection = new(
        "Item",
        i => new[] { ("title", (string?)i.Title), ("snippet", i.Snippet) },
        SourceUrl: i => i.Url,
        ImageUrl: i => i.Image,
        WithImageUrl: (i, url) => i with { Image = url });

    /// <summary>A scripted classifier: returns exactly the verdicts it was given.</summary>
    private sealed class FakeClassifier : IHalalClassifier
    {
        private readonly Func<IReadOnlyList<HalalCandidate>, IReadOnlyList<HalalVerdict>> _respond;
        public List<IReadOnlyList<HalalCandidate>> Calls { get; } = new();
        public bool IsConfigured => true;

        public FakeClassifier(Func<IReadOnlyList<HalalCandidate>, IReadOnlyList<HalalVerdict>> respond) =>
            _respond = respond;

        public Task<IReadOnlyList<HalalVerdict>> ClassifyAsync(
            IReadOnlyList<HalalCandidate> items, CancellationToken ct = default)
        {
            Calls.Add(items);
            return Task.FromResult(_respond(items));
        }
    }

    private sealed class ThrowingClassifier : IHalalClassifier
    {
        public bool IsConfigured => true;
        public Task<IReadOnlyList<HalalVerdict>> ClassifyAsync(
            IReadOnlyList<HalalCandidate> items, CancellationToken ct = default) =>
            throw new HttpRequestException("provider down");
    }

    private sealed class FakeImageClassifier : IHalalImageClassifier
    {
        private readonly Func<IReadOnlyList<string>, IReadOnlyList<ImageVerdict>> _respond;
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public bool IsConfigured => true;

        public FakeImageClassifier(Func<IReadOnlyList<string>, IReadOnlyList<ImageVerdict>> respond) =>
            _respond = respond;

        public Task<IReadOnlyList<ImageVerdict>> ClassifyAsync(
            IReadOnlyList<string> imageUrls, CancellationToken ct = default)
        {
            Calls.Add(imageUrls);
            return Task.FromResult(_respond(imageUrls));
        }
    }

    // ── Keyword-only path (no LLM configured) ─────────────────────────────────

    [Fact]
    public async Task KeywordOnly_RemovesFlaggedItems_AndRecordsRichFindings()
    {
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter);
        var items = new[]
        {
            new Item("Samsung Fridge", "energy efficient", "https://store.example/fridge"),
            new Item("Imported Beer 6-pack", "cold lager", "https://store.example/beer")
        };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().ContainSingle().Which.Title.Should().Be("Samsung Fridge");
        var finding = filter.AuditDetails.Should().ContainSingle().Subject;
        finding.Category.Should().Be("alcohol");
        finding.Rule.Should().Be("beer");
        finding.Field.Should().Be("title");
        finding.SourceUrl.Should().Be("https://store.example/beer");
        finding.Source.Should().Be(FindingSource.Keyword);
        finding.Confidence.Should().Be(1.0);
        finding.ItemRemoved.Should().BeTrue();
        finding.ContentHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task KeywordOnly_ReportsTheFieldThatMatched()
    {
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter);
        var items = new[] { new Item("Weekend brunch menu", "wine pairing included") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().BeEmpty();
        filter.AuditDetails.Single().Field.Should().Be("snippet");
    }

    // ── Whitelist (the admin "undo" feedback) ─────────────────────────────────

    [Fact]
    public async Task WhitelistedByContentHash_BypassesKeywordFilter()
    {
        var text = "Imported Beer 6-pack · cold lager";
        var filter = new ContentFilter(FilterStrictness.Strict,
            new[] { ModerationKeys.HashContent(text) });
        var moderator = new HalalModerator(filter);
        var items = new[] { new Item("Imported Beer 6-pack", "cold lager") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().ContainSingle();
        filter.AuditDetails.Should().BeEmpty();
    }

    [Fact]
    public async Task WhitelistedBySourceUrl_BypassesKeywordFilter()
    {
        var filter = new ContentFilter(FilterStrictness.Strict, new[] { "https://store.example/beer" });
        var moderator = new HalalModerator(filter);
        var items = new[] { new Item("Imported Beer 6-pack", Url: "https://store.example/beer") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().ContainSingle();
    }

    [Fact]
    public async Task WhitelistedItems_AreNotSentToTheClassifier()
    {
        var classifier = new FakeClassifier(_ => Array.Empty<HalalVerdict>());
        var filter = new ContentFilter(FilterStrictness.Strict, new[] { "https://store.example/beer" });
        var moderator = new HalalModerator(filter, classifier);
        var items = new[] { new Item("Imported Beer 6-pack", Url: "https://store.example/beer") };

        await moderator.ModerateAsync(items, Projection);

        classifier.Calls.Should().BeEmpty(); // sole item was whitelisted — nothing to classify
    }

    // ── LLM adjudication of keyword flags ─────────────────────────────────────

    [Fact]
    public async Task LlmOverturn_KeepsKeywordFlaggedItem()
    {
        // "Barber shop" style false positive: keyword "bar" fires, the LLM overturns in context.
        var classifier = new FakeClassifier(candidates => candidates
            .Where(c => c.KeywordCategory is not null)
            .Select(c => new HalalVerdict(c.Id, IsHaram: false, null, 0.95, "hotel near a nightlife district"))
            .ToList());
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, classifier);
        var items = new[] { new Item("Grand Hotel near the Bar district", "sea view rooms") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().ContainSingle();
        filter.AuditDetails.Should().BeEmpty();
    }

    [Fact]
    public async Task LlmConfirm_RemovesItem_WithLlmConfidenceAndReason()
    {
        var classifier = new FakeClassifier(candidates => candidates
            .Where(c => c.KeywordCategory is not null)
            .Select(c => new HalalVerdict(c.Id, IsHaram: true, "alcohol", 0.92, "sells alcoholic drinks"))
            .ToList());
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, classifier);
        var items = new[] { new Item("City Bar & Lounge", "craft beers on tap") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().BeEmpty();
        var finding = filter.AuditDetails.Single();
        finding.Source.Should().Be(FindingSource.Llm);
        finding.Confidence.Should().Be(0.92);
        finding.Rule.Should().Be("sells alcoholic drinks");
    }

    [Fact]
    public async Task LlmSilenceOnKeywordFlag_KeepsRemoval_FailSafe()
    {
        // Classifier responds but skips the keyword-flagged item → the flag stands.
        var classifier = new FakeClassifier(_ => Array.Empty<HalalVerdict>());
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, classifier);
        var items = new[] { new Item("Imported Beer 6-pack") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().BeEmpty();
        filter.AuditDetails.Single().Source.Should().Be(FindingSource.Keyword);
    }

    [Fact]
    public async Task DuplicateVerdictIds_DoNotFaultModeration()
    {
        // A rogue classifier emitting duplicate ids must never throw out of ModerateAsync —
        // moderation post-processing faults would fault the whole search (the f1507fa class).
        var classifier = new FakeClassifier(candidates => candidates
            .SelectMany(c => new[]
            {
                new HalalVerdict(c.Id, false, null, 0.6, "first"),
                new HalalVerdict(c.Id, true, "alcohol", 0.9, "duplicate — haram wins")
            })
            .ToList());
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, classifier);
        var items = new[] { new Item("City Bar & Lounge") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().BeEmpty("the haram duplicate wins over the halal one");
        filter.AuditDetails.Should().ContainSingle();
    }

    [Fact]
    public async Task LlmFailure_FallsBackToKeywordDecisions()
    {
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, new ThrowingClassifier());
        var items = new[]
        {
            new Item("Samsung Fridge"),
            new Item("Imported Beer 6-pack")
        };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().ContainSingle().Which.Title.Should().Be("Samsung Fridge");
    }

    // ── LLM-only flags (context the keyword list can't see) ───────────────────

    [Fact]
    public async Task LlmOnlyFlag_AboveThreshold_RemovesItem()
    {
        var classifier = new FakeClassifier(candidates => candidates
            .Where(c => c.Text.Contains("Ladies night"))
            .Select(c => new HalalVerdict(c.Id, true, "adult", 0.9, "nightlife event promotion"))
            .ToList());
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, classifier);
        var items = new[]
        {
            new Item("Samsung Fridge"),
            new Item("Ladies night at Sky Lounge")
        };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().ContainSingle().Which.Title.Should().Be("Samsung Fridge");
        filter.AuditDetails.Single().Source.Should().Be(FindingSource.Llm);
    }

    [Fact]
    public async Task LlmOnlyFlag_BelowThreshold_KeepsItem()
    {
        var classifier = new FakeClassifier(candidates => candidates
            .Select(c => new HalalVerdict(c.Id, true, "adult", 0.55, "maybe"))
            .ToList());
        var filter = new ContentFilter(FilterStrictness.Strict);
        // Default threshold 0.75 > 0.55 → not removed.
        var moderator = new HalalModerator(filter, classifier);
        var items = new[] { new Item("Evening event tickets") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().ContainSingle();
        filter.AuditDetails.Should().BeEmpty();
    }

    [Fact]
    public async Task FeedbackTunedThreshold_ChangesRemovalDecision()
    {
        var classifier = new FakeClassifier(candidates => candidates
            .Select(c => new HalalVerdict(c.Id, true, "adult", 0.6, "borderline"))
            .ToList());
        var filter = new ContentFilter(FilterStrictness.Strict);
        // Admin ratings said the model is precise for "adult" → lowered threshold admits 0.6.
        var policy = new HalalPolicy
        {
            CategoryThresholds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["adult"] = 0.5
            }
        };
        var moderator = new HalalModerator(filter, classifier, policy: policy);

        var kept = await moderator.ModerateAsync(new[] { new Item("Evening event tickets") }, Projection);

        kept.Should().BeEmpty();
    }

    // ── Image screening (granular: strip the image, keep the item) ────────────

    [Fact]
    public async Task FlaggedImage_IsStripped_ItemKept_FindingRecorded()
    {
        var imageClassifier = new FakeImageClassifier(urls => urls
            .Select(u => new ImageVerdict(u, true, "immodest", 0.9, "model not in modest dress"))
            .ToList());
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, imageClassifier: imageClassifier);
        var items = new[]
        {
            new Item("Summer dress", Url: "https://shop.example/dress", Image: "https://img.example/dress.jpg")
        };

        var kept = await moderator.ModerateAsync(items, Projection);

        var item = kept.Should().ContainSingle().Subject;
        item.Image.Should().BeNull("the flagged image is stripped, not the item");
        var finding = filter.AuditDetails.Single();
        finding.ItemRemoved.Should().BeFalse();
        finding.Field.Should().Be("image");
        finding.ImageUrl.Should().Be("https://img.example/dress.jpg");
        finding.SourceUrl.Should().Be("https://shop.example/dress");
        finding.Source.Should().Be(FindingSource.Vision);
        filter.AuditLog.Should().BeEmpty("image-strip findings must not count as removals");
    }

    [Fact]
    public async Task CleanImages_AreUntouched()
    {
        var imageClassifier = new FakeImageClassifier(_ => Array.Empty<ImageVerdict>());
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, imageClassifier: imageClassifier);
        var items = new[] { new Item("Samsung Fridge", Image: "https://img.example/fridge.jpg") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Single().Image.Should().Be("https://img.example/fridge.jpg");
    }

    [Fact]
    public async Task WhitelistedImage_IsNotSentToVision()
    {
        var imageClassifier = new FakeImageClassifier(urls => urls
            .Select(u => new ImageVerdict(u, true, "immodest", 0.99, "flagged"))
            .ToList());
        var filter = new ContentFilter(FilterStrictness.Strict, new[] { "https://img.example/dress.jpg" });
        var moderator = new HalalModerator(filter, imageClassifier: imageClassifier);
        var items = new[] { new Item("Summer dress", Image: "https://img.example/dress.jpg") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Single().Image.Should().Be("https://img.example/dress.jpg");
        imageClassifier.Calls.SelectMany(c => c).Should().BeEmpty();
    }

    [Fact]
    public async Task ImageBudget_CapsHowManyImagesAreScreenedPerRun()
    {
        var imageClassifier = new FakeImageClassifier(_ => Array.Empty<ImageVerdict>());
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, imageClassifier: imageClassifier,
            policy: new HalalPolicy { MaxImagesPerRun = 3 });
        var items = Enumerable.Range(0, 5)
            .Select(i => new Item($"Product {i}", Image: $"https://img.example/{i}.jpg"))
            .ToArray();

        await moderator.ModerateAsync(items, Projection);
        await moderator.ModerateAsync(items, Projection); // budget exhausted on the second run

        imageClassifier.Calls.SelectMany(c => c).Should().HaveCount(3);
    }

    // ── Off switch and riba policy ─────────────────────────────────────────────

    [Fact]
    public async Task StrictnessOff_SkipsEverything()
    {
        var classifier = new ThrowingClassifier(); // would throw if consulted
        var filter = new ContentFilter(FilterStrictness.Off);
        var moderator = new HalalModerator(filter, classifier);
        var items = new[] { new Item("Beer and pork and gambling") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().ContainSingle();
    }

    [Fact]
    public async Task BankItems_AreNeverRemoved_EvenWithClassifierConfigured()
    {
        // A classifier that (wrongly) tries to flag riba is neutralized by verdict sanitation
        // upstream in LlmHalalClassifier; here the moderator gets no verdict for the bank, and
        // the keyword list has no riba category — so the bank always survives.
        var classifier = new FakeClassifier(_ => Array.Empty<HalalVerdict>());
        var filter = new ContentFilter(FilterStrictness.Strict);
        var moderator = new HalalModerator(filter, classifier);
        var items = new[] { new Item("Arab Bank — personal loans", "0% interest installments") };

        var kept = await moderator.ModerateAsync(items, Projection);

        kept.Should().ContainSingle();
    }
}
