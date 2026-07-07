using System.Security.Cryptography;
using System.Text;

namespace Daleel.Core.Moderation;

/// <summary>How a moderation finding was decided.</summary>
public enum FindingSource
{
    /// <summary>Deterministic bilingual keyword/regex match (the always-on baseline).</summary>
    Keyword,

    /// <summary>Context-aware text classification by an LLM.</summary>
    Llm,

    /// <summary>Vision-model classification of an individual image.</summary>
    Vision
}

/// <summary>
/// One structured moderation finding: exactly what was flagged (snippet and/or image), where it
/// was found (which projected field), where it came from (source URL), how it was decided
/// (keyword rule vs LLM vs vision, with confidence), and whether the item was removed or only
/// its image stripped. This is the unit the admin review UI rates and whitelists.
/// </summary>
public sealed record FilterFinding(
    string Category,
    string Rule,
    string Kind,
    string Content,
    string? Field,
    string? SourceUrl,
    string? ImageUrl,
    double Confidence,
    FindingSource Source,
    string? ContentHash,
    bool ItemRemoved);

/// <summary>One item sent to the LLM text classifier.</summary>
/// <param name="Id">Caller-assigned index used to correlate verdicts back to items.</param>
/// <param name="Text">The item's filterable text (title/snippet/seller etc.).</param>
/// <param name="Kind">Item type hint, e.g. "SearchResult" or "StoreLocation".</param>
/// <param name="KeywordCategory">Category the keyword pass flagged, if any — the LLM adjudicates it.</param>
/// <param name="KeywordRule">The exact keyword that fired, so the LLM sees why it was flagged.</param>
public sealed record HalalCandidate(int Id, string Text, string Kind, string? KeywordCategory = null, string? KeywordRule = null);

/// <summary>The LLM's verdict for one <see cref="HalalCandidate"/>.</summary>
public sealed record HalalVerdict(int Id, bool IsHaram, string? Category, double Confidence, string? Reason);

/// <summary>
/// A classification round's outcome. <see cref="UnansweredIds"/> carries the candidates whose
/// batch FAILED (transport error, empty/unparseable output) — for those, no decision was made
/// and the caller must fall back to its deterministic baseline. A candidate absent from both
/// lists was genuinely considered and left unflagged (a deliberate skip).
/// </summary>
public sealed record HalalClassifierResult(
    IReadOnlyList<HalalVerdict> Verdicts,
    IReadOnlyCollection<int> UnansweredIds)
{
    public static readonly HalalClassifierResult Empty =
        new(Array.Empty<HalalVerdict>(), Array.Empty<int>());
}

/// <summary>A vision verdict for one image URL.</summary>
public sealed record ImageVerdict(string ImageUrl, bool IsHaram, string? Category, double Confidence, string? Reason);

/// <summary>
/// The outcome of screening a set of image URLs. <see cref="Flagged"/> are the images the model judged
/// haram; <see cref="Unscreened"/> are URLs the screen could NOT run on (billing/infra failure — e.g.
/// OpenRouter HTTP 402, a 5xx, or a timeout) and which therefore must be treated as NOT cleared. Any URL
/// absent from both was screened and is clean. Mirrors <see cref="HalalClassifierResult.UnansweredIds"/>
/// for text — it exists so a caller can tell "verified clean" from "could not verify" and fail CLOSED on
/// the latter (hide the image, keep it queued) instead of mistaking an outage for "no haram found".
/// </summary>
public sealed record ImageClassifierResult(
    IReadOnlyList<ImageVerdict> Flagged,
    IReadOnlyCollection<string> Unscreened)
{
    public static readonly ImageClassifierResult Empty =
        new(Array.Empty<ImageVerdict>(), Array.Empty<string>());
}

/// <summary>
/// Context-aware halal text classification. Implementations batch many items into few LLM calls
/// and must never throw for content reasons — a failed call returns an empty verdict list so the
/// caller falls back to keyword-only decisions.
/// </summary>
public interface IHalalClassifier
{
    /// <summary>True when a backing model is available and classification will actually run.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Classifies the candidates. Returns verdicts for items the model judged haram, plus explicit
    /// halal verdicts for keyword-flagged items it overturns — and, crucially, the ids whose batch
    /// failed, so the caller can apply its deterministic fallback to exactly those items instead
    /// of mistaking an infrastructure failure for a deliberate model skip.
    /// </summary>
    Task<HalalClassifierResult> ClassifyAsync(IReadOnlyList<HalalCandidate> items, CancellationToken ct = default);
}

/// <summary>
/// Vision-based halal classification of individual images (product photos, thumbnails).
/// Used to flag a single image — alcohol, pork, immodest dress — without removing the item it
/// belongs to. Unlike the text classifier's fail-OPEN contract, a failed vision call reports its
/// URLs as <see cref="ImageClassifierResult.Unscreened"/> so the caller can fail CLOSED (hide + hold).
/// </summary>
public interface IHalalImageClassifier
{
    /// <summary>True when a vision-capable model is available.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Screens each image URL and returns the haram <see cref="ImageClassifierResult.Flagged"/> ones plus
    /// the <see cref="ImageClassifierResult.Unscreened"/> URLs the screen could not run on (billing/infra
    /// failure). A URL in neither set was screened clean.
    /// </summary>
    Task<ImageClassifierResult> ClassifyAsync(IReadOnlyList<string> imageUrls, CancellationToken ct = default);
}

/// <summary>Inert image classifier used when no vision-capable key is configured.</summary>
public sealed class NullHalalImageClassifier : IHalalImageClassifier
{
    public bool IsConfigured => false;

    // No model configured = moderation intentionally off (a deployment choice, not a failure): report
    // nothing flagged AND nothing unscreened, so callers (which also guard on IsConfigured) show images
    // as-is. Fail-CLOSED is scoped to a configured screen that can't RUN, never to "no screen at all".
    public Task<ImageClassifierResult> ClassifyAsync(
        IReadOnlyList<string> imageUrls, CancellationToken ct = default) =>
        Task.FromResult(ImageClassifierResult.Empty);
}

/// <summary>
/// Tunable moderation thresholds, adjusted over time from admin correct/incorrect ratings
/// (the feedback loop). A category with many "incorrect" ratings gets a HIGHER removal
/// threshold — the classifier must be more confident before we hide an item of that kind.
/// </summary>
public sealed record HalalPolicy
{
    /// <summary>The canonical categories the classifiers may return; anything else is discarded.</summary>
    public static readonly IReadOnlySet<string> AllowedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "alcohol", "pork", "gambling", "adult", "drugs", "tobacco", "immodest"
    };

    /// <summary>
    /// Categories that must NEVER be filtered, whatever a model says. A store's financing model
    /// (riba/interest) is not haram content — the user can walk in and pay cash. See the policy
    /// note on <see cref="ContentFilter"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverFiltered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "riba", "interest", "banking", "bank", "finance", "financial", "insurance", "loans", "mortgage"
    };

    /// <summary>
    /// Minimum model confidence to remove an item when no per-category threshold applies.
    /// Deliberately high: showing a questionable listing costs less than hiding a legitimate
    /// one, so anything below the bar is shown (and recorded for admin rating).
    /// </summary>
    public double DefaultThreshold { get; init; } = 0.8;

    /// <summary>Per-category removal thresholds learned from admin ratings.</summary>
    public IReadOnlyDictionary<string, double> CategoryThresholds { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Max images sent to the vision classifier per run (cost guard).</summary>
    public int MaxImagesPerRun { get; init; } = 24;

    public double ThresholdFor(string category) =>
        CategoryThresholds.TryGetValue(category, out var t) ? t : DefaultThreshold;

    /// <summary>
    /// Derives a per-category threshold from admin feedback: precision = correct / rated.
    /// Perfect precision trusts the model down to 0.65; poor precision demands near-certainty.
    /// The floor stays above chance on purpose — even a well-rated category keeps the
    /// show-by-default bias; only clear-cut flags block. Categories with fewer than
    /// <paramref name="minSample"/> ratings keep the default.
    /// </summary>
    public static double ThresholdFromPrecision(int correct, int incorrect, double fallback = 0.8, int minSample = 5)
    {
        var rated = correct + incorrect;
        if (rated < minSample)
        {
            return fallback;
        }

        var precision = (double)correct / rated;
        return Math.Clamp(0.65 + (1.0 - precision) * 0.3, 0.65, 0.95);
    }
}

/// <summary>Stable keys used to whitelist specific findings (the admin "undo" action).</summary>
public static class ModerationKeys
{
    /// <summary>
    /// A deterministic hash of an item's filterable text — lowercased, whitespace-collapsed,
    /// SHA-256, hex. Whitelisting by hash un-hides the same content wherever it reappears.
    /// </summary>
    public static string HashContent(string? text)
    {
        var normalized = NormalizeForHash(text);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeForHash(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }
}
