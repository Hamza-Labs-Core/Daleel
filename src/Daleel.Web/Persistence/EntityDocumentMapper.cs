using Daleel.Core.Models;
using Daleel.Core.Persistence;
using Daleel.Web.Data;

namespace Daleel.Web.Persistence;

/// <summary>
/// Projects the in-memory extraction output (<see cref="ProductSearchResult"/> / <see cref="ProductModel"/>)
/// into self-contained <see cref="EntityDocument"/>s ready for R2. Pure and deterministic — no R2 or DB
/// access — so the mapping (especially that every relation ID is embedded) is unit-testable in isolation.
/// </summary>
public static class EntityDocumentMapper
{
    /// <summary>Maps each model in a result into an intent-tagged entity document.</summary>
    public static IReadOnlyList<EntityDocument> ToDocuments(
        ProductSearchResult result, SearchIntentType intent, string? searchId, DateTimeOffset capturedAt)
    {
        if (result.Models.Count == 0)
        {
            return Array.Empty<EntityDocument>();
        }

        var docs = new List<EntityDocument>(result.Models.Count);
        foreach (var m in result.Models)
        {
            docs.Add(ToDocument(m, intent, result, searchId, capturedAt));
        }

        return docs;
    }

    /// <summary>Maps one model into a fully self-contained document, embedding all of its relation IDs.</summary>
    public static EntityDocument ToDocument(
        ProductModel m, SearchIntentType intent, ProductSearchResult result, string? searchId, DateTimeOffset capturedAt)
    {
        var name = string.IsNullOrWhiteSpace(m.Name) ? (m.Model ?? string.Empty) : m.Name;
        var id = StableId.ForEntity(intent, m.Brand, m.Model, name);

        // Embedded relation IDs — make the document readable on its own (the same IDs are mirrored onto
        // the Postgres index row for traversal).
        var brandId = string.IsNullOrWhiteSpace(m.Brand) ? null : StableId.ForBrand(m.Brand);
        // A product is sold by many stores (its offers), so it has no single store relation; a service or
        // place IS a provider/venue, so it carries the stable store id of that entity.
        var storeId = intent == SearchIntentType.Product ? null : StableId.ForStore(name);
        // Tie products back to the profile/price rows stored under the shared normalized brand+model key.
        var productKey = intent == SearchIntentType.Product
            ? ProductProfile.KeyFor(m.Brand, m.Model, name)
            : null;

        return new EntityDocument
        {
            Id = id,
            Intent = intent,
            Name = name,
            Brand = m.Brand,
            Model = m.Model,
            ImageUrl = m.ImageUrl,
            ImageUrls = m.ImageUrl is { Length: > 0 } img ? new[] { img } : Array.Empty<string>(),
            Geo = result.Geo,
            Country = result.Country,
            Query = result.Query,
            Category = string.IsNullOrWhiteSpace(result.Strategy?.Product) ? null : result.Strategy!.Product.Trim(),
            SearchId = searchId,
            BrandId = brandId,
            StoreId = storeId,
            ProductKey = productKey,
            ParentProductKey = null,
            Specs = m.Specs.Count == 0
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(m.Specs, StringComparer.OrdinalIgnoreCase),
            Offers = m.Offers.Count == 0
                ? Array.Empty<EntityOffer>()
                : m.Offers.Select(o => new EntityOffer
                {
                    Source = o.Source,
                    Price = o.Price,
                    Currency = o.Currency,
                    Url = o.Url,
                    Condition = o.Condition
                }).ToList(),
            Pros = m.Pros,
            Cons = m.Cons,
            Summary = m.ReviewSummary,
            CapturedAt = capturedAt
        };
    }
}
