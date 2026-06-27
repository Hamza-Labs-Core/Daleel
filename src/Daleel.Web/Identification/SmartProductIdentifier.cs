using System.Security.Cryptography;
using System.Text;
using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Profiles;

namespace Daleel.Web.Identification;

/// <summary>The outcome of identifying a store listing against the brand-model database.</summary>
public sealed record ProductIdentification(
    int? BrandModelId, string? CanonicalModelName, string? Category, double Confidence, string Method)
{
    /// <summary>The "couldn't identify" result.</summary>
    public static ProductIdentification None { get; } = new(null, null, null, 0.0, "none");

    /// <summary>True when a brand model was matched.</summary>
    public bool Matched => BrandModelId is not null;
}

/// <summary>
/// Identifies which canonical brand model a (often vaguely-named) store listing actually is. The pipeline,
/// cheapest-first: (1) a text match against the brand-model database; (2) if that misses, discover the
/// brand's models across regions and retry the text match; (3) finally, a vision match of the store's photo
/// against the brand's catalogue images — memoized so the same image pair is never compared twice.
/// </summary>
public interface IProductIdentifier
{
    Task<ProductIdentification> IdentifyAsync(ProductModel item, CancellationToken ct = default);
}

public sealed class SmartProductIdentifier : IProductIdentifier
{
    /// <summary>Cap on vision comparisons per item — each is a paid LLM call.</summary>
    private const int MaxVisionComparisons = 8;

    /// <summary>A vision confidence at/above this short-circuits the remaining comparisons.</summary>
    private const double EarlyAcceptConfidence = 0.9;

    private readonly IBrandRepository _brands;
    private readonly IBrandModelRepository _models;
    private readonly IBrandCatalogSearcher _searcher;
    private readonly IVisionMatcher _vision;
    private readonly IVisionMatchCacheRepository _cache;
    private readonly ProfileOptions _options;
    private readonly ILogger<SmartProductIdentifier> _logger;

    public SmartProductIdentifier(
        IBrandRepository brands, IBrandModelRepository models, IBrandCatalogSearcher searcher,
        IVisionMatcher vision, IVisionMatchCacheRepository cache, ProfileOptions options,
        ILogger<SmartProductIdentifier> logger)
    {
        _brands = brands;
        _models = models;
        _searcher = searcher;
        _vision = vision;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public async Task<ProductIdentification> IdentifyAsync(ProductModel item, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.Brand))
        {
            return ProductIdentification.None; // no brand to scope the search — nothing to match against
        }

        var brand = await _brands.GetByNameAsync(item.Brand, ct).ConfigureAwait(false);
        if (brand is null)
        {
            return ProductIdentification.None;
        }

        // 1. Text match against whatever we already know about this brand's models.
        var known = await _models.ListByBrandAsync(brand.Id, ct).ConfigureAwait(false);
        if (TextMatch(item, known) is { } textHit)
        {
            return new ProductIdentification(textHit.Id, textHit.ModelName, textHit.Category, 1.0, "text");
        }

        // 2. Miss → discover the brand's models across regions, then retry the text match.
        var discovered = await SafeDiscoverAsync(brand, ct).ConfigureAwait(false);
        if (TextMatch(item, discovered) is { } discoveredHit)
        {
            return new ProductIdentification(discoveredHit.Id, discoveredHit.ModelName, discoveredHit.Category, 1.0, "text");
        }

        // 3. Vision match the store photo against the catalogue images.
        return await VisionMatchAsync(item, discovered, ct).ConfigureAwait(false);
    }

    /// <summary>Matches by normalized model name first, then by any recorded regional alias.</summary>
    private static BrandModel? TextMatch(ProductModel item, IReadOnlyList<BrandModel> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var probe in new[] { item.Model, item.Name })
        {
            if (string.IsNullOrWhiteSpace(probe))
            {
                continue;
            }

            var key = BrandModel.Normalize(probe);
            var byKey = candidates.FirstOrDefault(m => m.ModelKey == key);
            if (byKey is not null)
            {
                return byKey;
            }

            var byAlias = candidates.FirstOrDefault(m =>
                m.RegionalAliases.Any(a => BrandModel.Normalize(a) == key));
            if (byAlias is not null)
            {
                return byAlias;
            }
        }

        return null;
    }

    private async Task<ProductIdentification> VisionMatchAsync(
        ProductModel item, IReadOnlyList<BrandModel> candidates, CancellationToken ct)
    {
        if (!_vision.IsConfigured || string.IsNullOrWhiteSpace(item.ImageUrl))
        {
            return ProductIdentification.None;
        }

        var storeHash = HashImage(item.ImageUrl);
        var best = ProductIdentification.None;
        var comparisons = 0;

        foreach (var model in OrderForVision(item, candidates))
        {
            if (comparisons >= MaxVisionComparisons)
            {
                break;
            }

            var brandImage = model.ImageR2Urls.FirstOrDefault() ?? model.ImageUrl;
            if (string.IsNullOrWhiteSpace(brandImage))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            var (confidence, matchedName) = await ComparePairAsync(storeHash, item.ImageUrl!, model, brandImage, ct)
                .ConfigureAwait(false);
            comparisons++;

            if (confidence > best.Confidence)
            {
                best = new ProductIdentification(model.Id, matchedName ?? model.ModelName, model.Category, confidence, "vision");
            }

            if (confidence >= EarlyAcceptConfidence)
            {
                break; // confident enough — stop burning vision calls
            }
        }

        // Only report a vision identification when it clears the match threshold; otherwise it's a non-match.
        return best.Confidence >= VisionMatchCache.MatchThreshold ? best : ProductIdentification.None;
    }

    /// <summary>Returns the cached verdict for a pair, or runs the vision model once and caches it.</summary>
    private async Task<(double Confidence, string? MatchedName)> ComparePairAsync(
        string storeHash, string storeImageUrl, BrandModel model, string brandImage, CancellationToken ct)
    {
        var cached = await _cache.GetAsync(storeHash, model.Id, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return (cached.Confidence, cached.MatchedModelName);
        }

        var result = await _vision.CompareAsync(storeImageUrl, brandImage, model.ModelName, ct).ConfigureAwait(false);
        var confidence = result.SameProduct ? result.Confidence : Math.Min(result.Confidence, 0.0);

        await SafeCacheAsync(new VisionMatchCache
        {
            StoreImageHash = storeHash,
            BrandModelId = model.Id,
            Confidence = confidence,
            MatchedModelName = result.ModelName,
            MatchedAt = _options.Now()
        }, ct).ConfigureAwait(false);

        return (confidence, result.ModelName);
    }

    /// <summary>Prioritizes models whose name shares tokens with the listing, and that have an image.</summary>
    private static IEnumerable<BrandModel> OrderForVision(ProductModel item, IReadOnlyList<BrandModel> candidates)
    {
        var tokens = Tokenize($"{item.Model} {item.Name}");
        return candidates
            .Where(m => !string.IsNullOrWhiteSpace(m.ImageR2Urls.FirstOrDefault() ?? m.ImageUrl))
            .OrderByDescending(m => Tokenize(m.ModelName).Count(tokens.Contains))
            .ThenByDescending(m => m.IsAvailable);
    }

    private static HashSet<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new HashSet<string>()
            : new HashSet<string>(
                text.ToLowerInvariant().Split(new[] { ' ', '-', '_', '/', ',' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);

    private async Task<IReadOnlyList<BrandModel>> SafeDiscoverAsync(Brand brand, CancellationToken ct)
    {
        try
        {
            return await _searcher.DiscoverAsync(brand, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cross-region discovery failed for {Brand}", brand.Name);
            return await _models.ListByBrandAsync(brand.Id, ct).ConfigureAwait(false);
        }
    }

    private async Task SafeCacheAsync(VisionMatchCache entry, CancellationToken ct)
    {
        try { await _cache.UpsertAsync(entry, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "Caching vision match failed for model {Id}", entry.BrandModelId); }
    }

    /// <summary>A stable 64-char hex hash of the store image URL — the cache key half.</summary>
    internal static string HashImage(string imageUrl) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl.Trim()))).ToLowerInvariant();
}
