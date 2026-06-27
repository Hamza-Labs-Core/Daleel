namespace Daleel.Web.Data;

/// <summary>
/// A memoized result of a single vision-model comparison between a store product image and a brand
/// catalogue model. Vision LLM calls are slow and paid, so every (store image, brand model) pair is
/// matched <em>once</em> and the verdict is kept forever — the smart identifier reads this cache before
/// ever calling the vision model again for the same pair.
/// </summary>
/// <remarks>
/// Keyed uniquely by (<see cref="StoreImageHash"/>, <see cref="BrandModelId"/>): the store image is
/// identified by a stable content/URL hash rather than its (often volatile) CDN URL, so the same photo
/// re-encountered under a different URL still hits the cache. A negative match (low confidence) is cached
/// too — there's no point re-asking the model about a pair it already rejected.
/// </remarks>
public sealed class VisionMatchCache
{
    public int Id { get; set; }

    /// <summary>Stable hash of the store product image (SHA-256 hex of its URL/bytes) — the cache key half.</summary>
    public string StoreImageHash { get; set; } = string.Empty;

    /// <summary>The brand catalogue model the store image was compared against (FK, cascade-deleted).</summary>
    public int BrandModelId { get; set; }

    /// <summary>Navigation to the compared <see cref="BrandModel"/>.</summary>
    public BrandModel? BrandModel { get; set; }

    /// <summary>Vision-model confidence that the two images are the same product, 0.0–1.0.</summary>
    public double Confidence { get; set; }

    /// <summary>The model name the vision model read off the brand image, when it returned one.</summary>
    public string? MatchedModelName { get; set; }

    public DateTimeOffset MatchedAt { get; set; }

    /// <summary>Confidence at or above which a cached comparison counts as a positive match.</summary>
    public const double MatchThreshold = 0.75;

    /// <summary>True when this cached comparison is a confident match.</summary>
    public bool IsMatch => Confidence >= MatchThreshold;
}
