using Daleel.Web.Data;
using Daleel.Web.Translation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Translation;

public class TranslationServiceTests
{
    // ── NeedsTranslation heuristic ───────────────────────────────────────────────
    [Theory]
    [InlineData("best phone", "ar", true)]   // English → Arabic: translate
    [InlineData("أفضل هاتف", "ar", false)]    // already Arabic → Arabic: skip
    [InlineData("أفضل هاتف", "en", true)]     // Arabic → English: translate
    [InlineData("best phone", "en", false)]   // already English → English: skip
    [InlineData("", "ar", false)]             // blank: never
    [InlineData("   ", "en", false)]
    public void NeedsTranslation_FollowsScriptVsTarget(string text, string lang, bool expected) =>
        TranslationService.NeedsTranslation(text, lang).Should().Be(expected);

    // ── Disabled (no key) is a transparent pass-through ──────────────────────────
    [Fact]
    public async Task Disabled_ReturnsOriginals_AndNeverCallsTranslator()
    {
        var translator = new FakeTranslator { IsConfigured = false };
        var svc = Build(translator, new FakeRepo());

        var result = await svc.TranslateAsync(new[] { "hello", "world" }, "ar");

        result.Should().Equal("hello", "world");
        translator.Calls.Should().BeEmpty();
        svc.Enabled.Should().BeFalse();
    }

    // ── Cache hit avoids the API entirely ────────────────────────────────────────
    [Fact]
    public async Task CacheHit_ReturnsCached_WithoutCallingTranslator()
    {
        var repo = new FakeRepo();
        var hash = Sha256("good phone");
        repo.Seed(hash, "ar", "هاتف جيد");
        var translator = new FakeTranslator();
        var svc = Build(translator, repo);

        var result = await svc.TranslateAsync("good phone", "ar");

        result.Should().Be("هاتف جيد");
        translator.Calls.Should().BeEmpty();
    }

    // ── Cache miss calls DeepL once, returns it, and persists it ─────────────────
    [Fact]
    public async Task CacheMiss_CallsTranslator_ReturnsAndCaches()
    {
        var repo = new FakeRepo();
        var translator = new FakeTranslator(); // echoes "AR::" + text
        var svc = Build(translator, repo);

        var result = await svc.TranslateAsync("good phone", "ar");

        result.Should().Be("AR::good phone");
        translator.Calls.Should().ContainSingle().Which.Should().Equal("good phone");
        repo.Saved.Should().ContainSingle()
            .Which.Should().Match<TranslationCacheEntry>(e =>
                e.TargetLang == "ar" && e.TranslatedText == "AR::good phone" && e.SourceHash == Sha256("good phone"));
    }

    // ── Identical texts are translated once and fanned back out (dedupe) ─────────
    [Fact]
    public async Task DuplicateTexts_TranslatedOnce_AppliedToAllSlots()
    {
        var translator = new FakeTranslator();
        var svc = Build(translator, new FakeRepo());

        var result = await svc.TranslateAsync(new[] { "phone", "phone", "phone" }, "ar");

        result.Should().Equal("AR::phone", "AR::phone", "AR::phone");
        translator.Calls.Should().ContainSingle().Which.Should().Equal("phone"); // one text, not three
    }

    // ── Mixed batch: only the entries not already in the target are translated ───
    [Fact]
    public async Task MixedBatch_OnlyTranslatesWhatNeedsIt_PreservingOrder()
    {
        var translator = new FakeTranslator();
        var svc = Build(translator, new FakeRepo());

        var result = await svc.TranslateAsync(new[] { "fridge", "ثلاجة كبيرة", "tv" }, "ar");

        result.Should().Equal("AR::fridge", "ثلاجة كبيرة", "AR::tv"); // Arabic middle item untouched
        translator.Calls.Should().ContainSingle().Which.Should().BeEquivalentTo(new[] { "fridge", "tv" });
    }

    // ── A translator failure degrades to the originals (never throws) ────────────
    [Fact]
    public async Task TranslatorThrows_FallsBackToOriginals()
    {
        var translator = new FakeTranslator { Throw = true };
        var svc = Build(translator, new FakeRepo());

        var result = await svc.TranslateAsync(new[] { "hello", "world" }, "ar");

        result.Should().Equal("hello", "world");
    }

    // ── A cache row older than MaxAge is treated as a miss ───────────────────────
    [Fact]
    public async Task StaleCacheRow_IsIgnored_AndReTranslated()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new TranslationOptions
        {
            ApiKey = "key", MaxAge = TimeSpan.FromDays(1), Now = () => now
        };
        var repo = new FakeRepo();
        // Seed a row created two days ago — older than the 1-day MaxAge.
        repo.Seed(Sha256("good phone"), "ar", "قديم", createdAt: now - TimeSpan.FromDays(2));
        var translator = new FakeTranslator();
        var svc = new TranslationService(translator, repo, options, NullLogger<TranslationService>.Instance);

        var result = await svc.TranslateAsync("good phone", "ar");

        result.Should().Be("AR::good phone"); // re-translated, not the stale "قديم"
        translator.Calls.Should().ContainSingle();
    }

    private static TranslationService Build(ITranslator translator, ITranslationRepository repo) =>
        new(translator, repo, new TranslationOptions { ApiKey = "key" }, NullLogger<TranslationService>.Instance);

    private static string Sha256(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class FakeTranslator : ITranslator
    {
        public bool IsConfigured { get; set; } = true;
        public bool Throw { get; set; }
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }
            Calls.Add(texts.ToList());
            return Task.FromResult<IReadOnlyList<string>>(
                texts.Select(t => $"{targetLang.ToUpperInvariant()}::{t}").ToList());
        }
    }

    private sealed class FakeRepo : ITranslationRepository
    {
        private readonly Dictionary<(string, string), (string Text, DateTimeOffset At)> _store = new();
        public List<TranslationCacheEntry> Saved { get; } = new();

        public void Seed(string hash, string lang, string text, DateTimeOffset? createdAt = null) =>
            _store[(hash, lang)] = (text, createdAt ?? DateTimeOffset.UtcNow);

        public Task<IReadOnlyDictionary<string, string>> GetFreshAsync(
            IReadOnlyCollection<string> sourceHashes, string targetLang, DateTimeOffset notOlderThan,
            CancellationToken ct = default)
        {
            var map = new Dictionary<string, string>();
            foreach (var hash in sourceHashes)
            {
                if (_store.TryGetValue((hash, targetLang), out var row) && row.At >= notOlderThan)
                {
                    map[hash] = row.Text;
                }
            }
            return Task.FromResult<IReadOnlyDictionary<string, string>>(map);
        }

        public Task SaveAsync(IReadOnlyCollection<TranslationCacheEntry> entries, CancellationToken ct = default)
        {
            Saved.AddRange(entries);
            foreach (var e in entries)
            {
                _store[(e.SourceHash, e.TargetLang)] = (e.TranslatedText, e.CreatedAt);
            }
            return Task.CompletedTask;
        }
    }
}
