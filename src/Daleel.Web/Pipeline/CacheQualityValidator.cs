using Daleel.Agent;
using Daleel.Core.Models;

namespace Daleel.Web.Pipeline;

/// <summary>
/// The kinds of data a cached report can be missing. A <see cref="FlagsAttribute"/> set so a single
/// <see cref="CacheQualityReport"/> can carry every shortcoming it found at once, and the
/// re-enrichment step can branch on exactly which dimensions to refill.
/// </summary>
[Flags]
public enum CacheGap
{
    None = 0,
    ProductImages = 1 << 0,
    ProductPrices = 1 << 1,
    Specs = 1 << 2,
    ProductModel = 1 << 3,
    ProductSku = 1 << 4,
    BrandLogo = 1 << 5,
    BrandDescription = 1 << 6,
    StoreLocation = 1 << 7,
    StoreContact = 1 << 8,
    StoreMaps = 1 << 9
}

/// <summary>What the smart cache should do with a hit, given its <see cref="CacheQualityReport"/>.</summary>
public enum CacheDecision
{
    /// <summary>Quality is at/above the bar — replay the cached report verbatim.</summary>
    ServeAsIs,

    /// <summary>Good enough to show immediately, but a background pass should refill the gaps.</summary>
    ServeAndEnrich,

    /// <summary>Too poor to count as a hit — treat as a miss and run the full search live.</summary>
    Miss
}

/// <summary>
/// The completeness verdict on a cached report: an overall 0–100 <see cref="Score"/>, the
/// <see cref="CacheGap"/> dimensions that fell short, a human-readable <see cref="Missing"/> list, and
/// the concrete <em>targets</em> a partial re-enrichment should refill — the product indexes worth a
/// deep-dive and the brand/store names worth re-researching. Plain data so it can ride on the pipeline
/// state and the run result without dragging services along.
/// </summary>
public sealed record CacheQualityReport(
    int Score,
    CacheGap Gaps,
    IReadOnlyList<string> Missing,
    IReadOnlyList<int> ThinProducts,
    IReadOnlyList<string> DeficientBrands,
    IReadOnlyList<string> DeficientStores)
{
    /// <summary>At/above this score a cache hit is served verbatim.</summary>
    public const int ServeThreshold = 80;

    /// <summary>Below this score a cache hit is discarded and the search runs live.</summary>
    public const int MissThreshold = 30;

    /// <summary>A perfect, nothing-to-do verdict (non-product answers — a plain conversational "ask").</summary>
    public static readonly CacheQualityReport Complete = new(
        100, CacheGap.None, Array.Empty<string>(), Array.Empty<int>(),
        Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// A product search that found nothing. Scored <see cref="MissThreshold"/>-low so the
    /// <see cref="CacheDecision.Miss"/> verdict re-runs the search live instead of replaying the empty
    /// payload. Empties are too often a transient/upstream failure (a provider outage, a geo-filter bug)
    /// rather than a true "nothing exists" — serving them stale for the full TTL hides the recovery.
    /// </summary>
    public static readonly CacheQualityReport Empty = new(
        0, CacheGap.None, new[] { "empty result — no products, brands or stores found" },
        Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>True when at least one gap is something re-enrichment can actually refill.</summary>
    public bool HasActionableGaps =>
        ThinProducts.Count > 0 || DeficientBrands.Count > 0 || DeficientStores.Count > 0;

    /// <summary>
    /// The cache action for this verdict. A mid-band score with no actionable target degrades to
    /// <see cref="CacheDecision.ServeAsIs"/> — re-enriching nothing would only burn a background pass.
    /// </summary>
    public CacheDecision Decision =>
        Score >= ServeThreshold ? CacheDecision.ServeAsIs
        : Score < MissThreshold ? CacheDecision.Miss
        : HasActionableGaps ? CacheDecision.ServeAndEnrich
        : CacheDecision.ServeAsIs;
}

/// <summary>
/// Scores a cached search report for completeness before it is replayed. The smart cache uses the
/// verdict to decide between serving the hit as-is, serving it but kicking off a background pass to
/// refill the gaps, or discarding it and running the search live.
/// </summary>
public interface ICacheQualityValidator
{
    /// <summary>Scores the completeness of a cached answer and lists what (if anything) is missing.</summary>
    CacheQualityReport Evaluate(AgentAnswer answer);
}

/// <summary>
/// Default validator. Scores the answer's product set as a weighted blend of three entity dimensions —
/// products (50%), brands (25%) and stores (25%) — and only counts a dimension when the answer actually
/// has entities of that kind, so a store-less product search isn't penalised for missing stores.
/// </summary>
/// <remarks>
/// The thresholds (name + ≥1 price + ≥1 image for products; logo for brands; location + a contact method
/// for stores; non-empty specs) come straight from the product UI's needs: a card with no image or no
/// price reads as broken. Pure, deterministic and side-effect-free, so it's cheap to call on every hit
/// and trivial to unit-test.
/// </remarks>
public sealed class CacheQualityValidator : ICacheQualityValidator
{
    // Dimension weights (relative; normalised by the dimensions actually present).
    private const double ProductWeight = 50;
    private const double BrandWeight = 25;
    private const double StoreWeight = 25;

    // A product carrying fewer than this many specs is "thin" — worth an official-site deep-dive. Mirrors
    // ItemEnrichmentService.ThinSpecThreshold so the validator's targeting matches what enrichment can fill.
    private const int RichSpecThreshold = 3;

    public CacheQualityReport Evaluate(AgentAnswer answer)
    {
        var products = answer.Products;

        // A non-product answer (a plain "ask") has no product completeness to validate, and re-enrichment
        // has nothing to act on — replay it verbatim.
        if (products is null)
        {
            return CacheQualityReport.Complete;
        }

        var models = products.Models;
        var brands = products.Brands;
        var stores = products.Stores;

        // A product query that found nothing: treat the cached empty as a MISS so the search re-runs live.
        // An empty payload is too often a transient/upstream failure (provider outage, geo-filter bug)
        // rather than a true "nothing exists"; replaying it for the whole TTL masks the recovery.
        if (models.Count == 0 && brands.Count == 0 && stores.Count == 0)
        {
            return CacheQualityReport.Empty;
        }

        var missing = new List<string>();
        var gaps = CacheGap.None;
        double weightedSum = 0, weightTotal = 0;

        var thinProducts = ScoreProducts(models, ref weightedSum, ref weightTotal, ref gaps, missing);
        var deficientBrands = ScoreBrands(brands, ref weightedSum, ref weightTotal, ref gaps, missing);
        var deficientStores = ScoreStores(stores, ref weightedSum, ref weightTotal, ref gaps, missing);

        var score = weightTotal <= 0 ? 100 : (int)Math.Round(100 * weightedSum / weightTotal);
        return new CacheQualityReport(score, gaps, missing, thinProducts, deficientBrands, deficientStores);
    }

    /// <summary>
    /// Per-product score (image 0.30, price 0.30, specs 0.25, model 0.10, SKU 0.05), averaged across the
    /// models. Returns the indexes of models worth a deep-dive — those missing an image or price, or with
    /// fewer than <see cref="RichSpecThreshold"/> specs.
    /// </summary>
    private static List<int> ScoreProducts(
        IReadOnlyList<ProductModel> models, ref double weightedSum, ref double weightTotal,
        ref CacheGap gaps, List<string> missing)
    {
        var thin = new List<int>();
        if (models.Count == 0)
        {
            return thin;
        }

        double sum = 0;
        int noImage = 0, noPrice = 0, noSpecs = 0, noModel = 0, noSku = 0;
        for (var i = 0; i < models.Count; i++)
        {
            var m = models[i];
            var hasImage = !string.IsNullOrWhiteSpace(m.ImageUrl);
            var hasPrice = m.Msrp is not null || m.Offers.Any(o => o.Price is not null);
            var specCount = m.Specs.Count(kv => !string.IsNullOrWhiteSpace(kv.Value));
            var hasModel = !string.IsNullOrWhiteSpace(m.Model);
            var hasSku = m.Specs.Keys.Any(IsSkuKey);

            var specScore = specCount >= RichSpecThreshold ? 1.0 : specCount >= 1 ? 0.5 : 0.0;
            sum += (hasImage ? 0.30 : 0) + (hasPrice ? 0.30 : 0) + specScore * 0.25
                 + (hasModel ? 0.10 : 0) + (hasSku ? 0.05 : 0);

            if (!hasImage) noImage++;
            if (!hasPrice) noPrice++;
            if (specCount == 0) noSpecs++;
            if (!hasModel) noModel++;
            if (!hasSku) noSku++;

            // A model is worth re-scraping when its core card data (image/price) or its specs are thin.
            if (!hasImage || !hasPrice || specCount < RichSpecThreshold)
            {
                thin.Add(i);
            }
        }

        weightedSum += sum / models.Count * ProductWeight;
        weightTotal += ProductWeight;

        if (noImage > 0) { gaps |= CacheGap.ProductImages; missing.Add($"{noImage} product(s) missing an image"); }
        if (noPrice > 0) { gaps |= CacheGap.ProductPrices; missing.Add($"{noPrice} product(s) missing a price"); }
        if (noSpecs > 0) { gaps |= CacheGap.Specs; missing.Add($"{noSpecs} product(s) missing specs"); }
        if (noModel > 0) { gaps |= CacheGap.ProductModel; missing.Add($"{noModel} product(s) missing a model name"); }
        if (noSku > 0) { gaps |= CacheGap.ProductSku; missing.Add($"{noSku} product(s) missing a SKU"); }
        return thin;
    }

    /// <summary>Per-brand score (logo 0.6, description 0.4). Returns names missing a logo or a description.</summary>
    private static List<string> ScoreBrands(
        IReadOnlyList<BrandInfo> brands, ref double weightedSum, ref double weightTotal,
        ref CacheGap gaps, List<string> missing)
    {
        var deficient = new List<string>();
        if (brands.Count == 0)
        {
            return deficient;
        }

        double sum = 0;
        int noLogo = 0, noDesc = 0;
        foreach (var b in brands)
        {
            var hasLogo = !string.IsNullOrWhiteSpace(b.LogoUrl);
            var hasDesc = b.Reputation is { } r
                && (!string.IsNullOrWhiteSpace(r.Summary) || r.Pros.Count > 0 || r.Complaints.Count > 0);
            sum += (hasLogo ? 0.6 : 0) + (hasDesc ? 0.4 : 0);

            if (!hasLogo) noLogo++;
            if (!hasDesc) noDesc++;
            if ((!hasLogo || !hasDesc) && !string.IsNullOrWhiteSpace(b.Name))
            {
                deficient.Add(b.Name);
            }
        }

        weightedSum += sum / brands.Count * BrandWeight;
        weightTotal += BrandWeight;

        if (noLogo > 0) { gaps |= CacheGap.BrandLogo; missing.Add($"{noLogo} brand(s) missing a logo"); }
        if (noDesc > 0) { gaps |= CacheGap.BrandDescription; missing.Add($"{noDesc} brand(s) missing a description"); }
        return deficient;
    }

    /// <summary>
    /// Per-store score (location 0.5, contact 0.3, Google Maps data 0.2). Returns names missing any of
    /// the three so store-research can re-verify them.
    /// </summary>
    private static List<string> ScoreStores(
        IReadOnlyList<StoreInfo> stores, ref double weightedSum, ref double weightTotal,
        ref CacheGap gaps, List<string> missing)
    {
        var deficient = new List<string>();
        if (stores.Count == 0)
        {
            return deficient;
        }

        double sum = 0;
        int noLocation = 0, noContact = 0, noMaps = 0;
        foreach (var s in stores)
        {
            var hasLocation = !string.IsNullOrWhiteSpace(s.Address) || s.HasLocation;
            var hasContact = !string.IsNullOrWhiteSpace(s.Phone) || !string.IsNullOrWhiteSpace(s.Url);
            var hasMaps = s.Rating is not null || s.ReviewCount is not null;
            sum += (hasLocation ? 0.5 : 0) + (hasContact ? 0.3 : 0) + (hasMaps ? 0.2 : 0);

            if (!hasLocation) noLocation++;
            if (!hasContact) noContact++;
            if (!hasMaps) noMaps++;
            if ((!hasLocation || !hasContact || !hasMaps) && !string.IsNullOrWhiteSpace(s.Name))
            {
                deficient.Add(s.Name);
            }
        }

        weightedSum += sum / stores.Count * StoreWeight;
        weightTotal += StoreWeight;

        if (noLocation > 0) { gaps |= CacheGap.StoreLocation; missing.Add($"{noLocation} store(s) missing a location"); }
        if (noContact > 0) { gaps |= CacheGap.StoreContact; missing.Add($"{noContact} store(s) missing a contact method"); }
        if (noMaps > 0) { gaps |= CacheGap.StoreMaps; missing.Add($"{noMaps} store(s) missing Google Maps data"); }
        return deficient;
    }

    // ProductModel has no dedicated SKU column, so a SKU is "present" when one of the merged spec keys
    // carries an identifier (sku / mpn / upc / ean / part-or-model number).
    private static readonly string[] SkuKeyHints = { "sku", "mpn", "upc", "ean", "part number", "model number" };

    private static bool IsSkuKey(string key) =>
        SkuKeyHints.Any(h => key.Contains(h, StringComparison.OrdinalIgnoreCase));
}
