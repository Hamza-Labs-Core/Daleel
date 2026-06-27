using System.Text.Json;
using Daleel.Core.Models;
using Daleel.Web.Data;

namespace Daleel.Web.Services;

/// <summary>
/// Assembles a product's detail view entirely from the persisted database — the harvested
/// <see cref="BrandModel"/> (specs + R2 image), the deep-dive <see cref="ProductProfile"/>
/// (description) and the <see cref="ScrapedPrice"/> time series (where-to-buy + price history) — so the
/// product page reads from saved data rather than re-scraping live on every visit.
/// </summary>
/// <remarks>
/// The bridge between the hashed <c>/product/{id}</c> route and real rows is the normalized "brand
/// model" key: link sites carry the human name as a query param, and <see cref="ProductProfile.Normalize"/>
/// of that name reconstructs the very key <see cref="ScrapedPrice"/>/<see cref="ProductProfile"/> are
/// stored under. A numeric route id resolves a <see cref="BrandModel"/> by its real database key directly.
/// </remarks>
public interface IProductDetailDbService
{
    /// <summary>
    /// Builds the full detail view for a product from the database. <paramref name="id"/> is the route
    /// segment (a numeric catalogue id or a stable-id hash); <paramref name="name"/> is the human name
    /// carried alongside it. <paramref name="lookupKey"/>, when supplied, is the exact stored
    /// <see cref="ScrapedPrice.ProductKey"/> and takes precedence over reconstructing the key from the name —
    /// a store page links by it so a product whose display name isn't "brand model" still resolves. Returns
    /// null when nothing about the product has been saved yet.
    /// </summary>
    Task<ProductDetailView?> GetAsync(
        string id, string name, string geo, string? lookupKey = null, CancellationToken ct = default);

    /// <summary>
    /// Builds a lightweight <see cref="ProductModel"/> (specs + offers) from the database for the
    /// side-by-side compare table. Returns null when the product has neither saved specs nor prices, so
    /// the caller can show a "specs not yet profiled" notice.
    /// </summary>
    Task<ProductModel?> GetComparableAsync(string name, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ProductDetailDbService : IProductDetailDbService
{
    private readonly IBrandModelRepository _models;
    private readonly IProductProfileRepository _profiles;
    private readonly IScrapedPriceRepository _prices;
    private readonly IBrandRepository _brands;

    public ProductDetailDbService(
        IBrandModelRepository models,
        IProductProfileRepository profiles,
        IScrapedPriceRepository prices,
        IBrandRepository brands)
    {
        _models = models;
        _profiles = profiles;
        _prices = prices;
        _brands = brands;
    }

    public async Task<ProductDetailView?> GetAsync(
        string id, string name, string geo, string? lookupKey = null, CancellationToken ct = default)
    {
        // Prefer the exact stored key when the caller has it (a store page links by ScrapedPrice.ProductKey),
        // since a product's display name often isn't "brand model" and Normalize(name) would miss the
        // price/profile rows. Normalize is idempotent, so an already-normalized key passes through unchanged.
        var key = ProductProfile.Normalize(
            !string.IsNullOrWhiteSpace(lookupKey) ? lookupKey! : name ?? string.Empty);

        // A numeric id is a real catalogue key — resolve it directly. Anything else (a stable-id hash)
        // can't index a row, so fall back to the normalized name shared with the price/profile tables.
        var brandModel = int.TryParse(id, out var dbId)
            ? await _models.GetByIdAsync(dbId, ct)
            : string.IsNullOrEmpty(key) ? null : await _models.FindByProductKeyAsync(key, ct);

        // When we resolved a real catalogue row, prefer its identity-derived key for the price/profile
        // lookups (a numeric route may carry a stale or absent name query param).
        if (brandModel is not null)
        {
            key = ProductProfile.KeyFor(brandModel.Brand?.Name, brandModel.ModelName, brandModel.ModelName);
        }

        var profile = string.IsNullOrEmpty(key) ? null : await _profiles.GetByKeyAsync(key, ct);
        var latest = string.IsNullOrEmpty(key)
            ? Array.Empty<ScrapedPrice>()
            : await _prices.LatestForProductAsync(key, ct);
        var history = string.IsNullOrEmpty(key)
            ? Array.Empty<ScrapedPrice>()
            : await _prices.HistoryForProductAsync(key, ct: ct);

        // Nothing about this product has been saved — the page shows a "not yet available" panel.
        if (brandModel is null && profile is null && latest.Count == 0)
        {
            return null;
        }

        var specs = ParseSpecs(brandModel?.SpecsJson);
        var description = profile?.Details;
        // A harvested "description" spec is prose, not a spec row: surface it as the description (when the
        // deep-dive profile didn't supply one) and keep it out of the specs table.
        if (specs.TryGetValue("description", out var specDescription))
        {
            specs = WithoutKey(specs, "description");
            description ??= specDescription;
        }

        var offers = latest
            .OrderBy(p => p.Price is null)          // priced offers first
            .ThenBy(p => p.Price)                    // then cheapest-first
            .Select(p => new ProductDetailOffer(
                p.StoreName,
                StableId.ForStore(p.StoreName),
                p.Price,
                p.Currency,
                p.SourceUrl,
                p.ScrapedAt))
            .ToList();

        var lowestOffer = offers.FirstOrDefault(o => o.Price is not null);
        var displayCurrency = lowestOffer?.Currency ?? brandModel?.Currency;

        // The observed range renders in a single currency, so only fold in history priced in that same
        // currency — otherwise a product whose history mixes e.g. USD and JOD shows a nonsensical min–max band.
        var observedPrices = history
            .Where(p => p.Price is not null &&
                (displayCurrency is null ||
                 string.Equals(p.Currency, displayCurrency, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Price!.Value)
            .ToList();

        // brandModel is always loaded with its Brand (Include), so reuse it rather than re-querying by id.
        var brand = brandModel?.Brand
            ?? (!string.IsNullOrWhiteSpace(profile?.Brand)
                ? await _brands.GetByNameAsync(profile!.Brand!, ct)
                : null);

        var brandName = brand?.Name ?? brandModel?.Brand?.Name ?? profile?.Brand;
        var displayName = brandModel?.ModelName ?? profile?.Name;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = name ?? string.Empty;
        }

        return new ProductDetailView
        {
            Name = displayName,
            Brand = brandName,
            Model = brandModel?.ModelName ?? profile?.Model,
            ImageUrl = brandModel?.ImageUrl,
            Description = description,
            Specs = specs,
            Offers = offers,
            LowestPrice = lowestOffer?.Price ?? brandModel?.LocalPrice,
            Currency = displayCurrency,
            LowestSeen = observedPrices.Count > 0 ? observedPrices.Min() : null,
            HighestSeen = observedPrices.Count > 0 ? observedPrices.Max() : null,
            LastUpdated = latest.Count > 0 ? latest.Max(p => p.ScrapedAt) : null,
            BrandStableId = brandName is not null ? StableId.ForBrand(brandName) : null,
            BrandReputationScore = brand?.ReputationScore,
            HasCatalogueProfile = brandModel is not null,
        };
    }

    public async Task<ProductModel?> GetComparableAsync(string name, CancellationToken ct = default)
    {
        var key = ProductProfile.Normalize(name ?? string.Empty);
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var brandModel = await _models.FindByProductKeyAsync(key, ct);
        var profile = await _profiles.GetByKeyAsync(key, ct);
        var latest = await _prices.LatestForProductAsync(key, ct);

        var specs = WithoutKey(ParseSpecs(brandModel?.SpecsJson), "description");
        if (specs.Count == 0 && latest.Count == 0)
        {
            return null; // nothing profiled — the compare page shows "specs not yet profiled".
        }

        var ordered = latest.OrderBy(p => p.Price is null).ThenBy(p => p.Price).ToList();
        var cheapest = ordered.FirstOrDefault(p => p.Price is not null)?.Price;
        var offers = ordered
            .Select(p => new PriceOffer
            {
                Source = p.StoreName,
                Price = p.Price,
                Currency = p.Currency,
                Url = p.SourceUrl,
                IsLowest = p.Price is { } price && price == cheapest,
            })
            .ToList();

        var displayName = brandModel?.ModelName ?? profile?.Name;
        return new ProductModel
        {
            Name = string.IsNullOrWhiteSpace(displayName) ? name ?? string.Empty : displayName,
            Brand = brandModel?.Brand?.Name ?? profile?.Brand,
            Model = brandModel?.ModelName ?? profile?.Model,
            ImageUrl = brandModel?.ImageUrl,
            Specs = specs,
            Offers = offers,
        };
    }

    /// <summary>Parses a <see cref="BrandModel.SpecsJson"/> object into a flat key→value map, tolerating
    /// non-string values and malformed JSON (returns what it can, never throws).</summary>
    private static Dictionary<string, string> ParseSpecs(string? json)
    {
        var specs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return specs;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return specs;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "Yes",
                    JsonValueKind.False => "No",
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => prop.Value.GetRawText(), // arrays/objects are rare here; keep the raw text rather than drop it.
                };
                if (!string.IsNullOrWhiteSpace(value))
                {
                    specs[prop.Name] = value!;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed specs blob — return whatever parsed; never surface a parse error to the page.
        }

        return specs;
    }

    private static Dictionary<string, string> WithoutKey(Dictionary<string, string> specs, string key) =>
        specs.Where(kv => !kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One place a product is currently available, from the latest <see cref="ScrapedPrice"/> per
/// store. <see cref="StoreId"/> is the stable id used to link through to the store's page.</summary>
public sealed record ProductDetailOffer(
    string StoreName,
    string StoreId,
    decimal? Price,
    string? Currency,
    string? Url,
    DateTimeOffset ScrapedAt);

/// <summary>The product detail page's view model, assembled entirely from saved data.</summary>
public sealed record ProductDetailView
{
    public required string Name { get; init; }
    public string? Brand { get; init; }
    public string? Model { get; init; }
    public string? ImageUrl { get; init; }

    /// <summary>Prose description from the deep-dive profile (or a harvested "description" spec).</summary>
    public string? Description { get; init; }

    public IReadOnlyDictionary<string, string> Specs { get; init; } = new Dictionary<string, string>();

    /// <summary>Where to buy: the current price per store, cheapest-first.</summary>
    public IReadOnlyList<ProductDetailOffer> Offers { get; init; } = Array.Empty<ProductDetailOffer>();

    public decimal? LowestPrice { get; init; }
    public string? Currency { get; init; }

    /// <summary>Lowest/highest price ever observed across the saved history (for the observed-range hint).</summary>
    public decimal? LowestSeen { get; init; }
    public decimal? HighestSeen { get; init; }

    public DateTimeOffset? LastUpdated { get; init; }

    public string? BrandStableId { get; init; }
    public double? BrandReputationScore { get; init; }

    /// <summary>True when a harvested catalogue row (specs + R2 image) backed this view, not just prices.</summary>
    public bool HasCatalogueProfile { get; init; }

    public int SellerCount => Offers.Count;

    public Money? Lowest =>
        LowestPrice is { } p && !string.IsNullOrWhiteSpace(Currency) ? new Money(p, Currency!) : null;
}
