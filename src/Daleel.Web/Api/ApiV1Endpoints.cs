using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Persistence;

namespace Daleel.Web.Api;

/// <summary>
/// The B2B data API (spec 2026-07-19-b2b-api-design), v1: read-only JSON endpoints over the entity
/// index, store profiles and brand profiles. Every route is key-authenticated, scope-gated and
/// credit-metered by <see cref="ApiKeyEndpointFilter"/>; repositories are injected per request
/// (transient — each handler call gets its own DbContext). Handlers are internal statics so the
/// tests exercise them directly without a full host.
/// </summary>
public static class ApiV1Endpoints
{
    /// <summary>Fixed page size for the list endpoints (loop-safety paging, not a result cap —
    /// every row is reachable by walking pages).</summary>
    public const int PageSize = 50;

    public static IEndpointRouteBuilder MapB2bApiV1(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/items", ListItemsAsync)
            .AddEndpointFilter(new ApiKeyEndpointFilter(
                ApiScopes.ItemsRead, "items.list", ApiPricing.ItemsListKey, ApiPricing.ItemsListDefault));

        v1.MapGet("/items/{id}", GetItemAsync)
            .AddEndpointFilter(new ApiKeyEndpointFilter(
                ApiScopes.ItemsRead, "items.get", ApiPricing.ItemDocKey, ApiPricing.ItemDocDefault));

        v1.MapGet("/stores", ListStoresAsync)
            .AddEndpointFilter(new ApiKeyEndpointFilter(
                ApiScopes.StoresRead, "stores.list", ApiPricing.StoresKey, ApiPricing.StoresDefault));

        v1.MapGet("/stores/{id:int}", GetStoreAsync)
            .AddEndpointFilter(new ApiKeyEndpointFilter(
                ApiScopes.StoresRead, "stores.get", ApiPricing.StoresKey, ApiPricing.StoresDefault));

        v1.MapGet("/brands", ListBrandsAsync)
            .AddEndpointFilter(new ApiKeyEndpointFilter(
                ApiScopes.BrandsRead, "brands.list", ApiPricing.BrandsKey, ApiPricing.BrandsDefault));

        v1.MapGet("/brands/{id:int}", GetBrandAsync)
            .AddEndpointFilter(new ApiKeyEndpointFilter(
                ApiScopes.BrandsRead, "brands.get", ApiPricing.BrandsKey, ApiPricing.BrandsDefault));

        return app;
    }

    // ── /items ────────────────────────────────────────────────────────────────

    /// <summary>Entity-index page. Live rows only — SearchAsync already excludes alias rows
    /// (MergedIntoId != null), so merged duplicates never surface here.</summary>
    internal static async Task<IResult> ListItemsAsync(
        IEntityRecordRepository entities,
        string? q = null, string? geo = null, string? category = null,
        int? brand = null, int? store = null, int page = 1,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var rows = await entities.SearchAsync(
            q, intent: null, skip: (page - 1) * PageSize, take: PageSize,
            geo: geo, category: category, brandId: brand, storeId: store, ct: ct);

        return Results.Json(new
        {
            page,
            pageSize = PageSize,
            count = rows.Count,
            items = rows.Select(ItemSummary.From)
        });
    }

    /// <summary>Full entity document (the R2 source of truth). Follows the alias chain first, so an
    /// old id whose entity was merged still resolves — to the survivor's document.</summary>
    internal static async Task<IResult> GetItemAsync(
        string id,
        IEntityRecordRepository entities,
        ISearchEntityStore store,
        CancellationToken ct = default)
    {
        var record = await entities.GetByIdAsync(id, ct);

        // Follow MergedIntoId aliases to the surviving entity (bounded — a merge chain is short by
        // construction, the bound only guards against a pathological cycle).
        for (var hop = 0; record?.MergedIntoId is { } survivorId && hop < 5; hop++)
        {
            record = await entities.GetByIdAsync(survivorId, ct);
        }

        if (record is null)
        {
            return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var intent = Enum.TryParse<SearchIntentType>(record.Intent, ignoreCase: true, out var parsed)
            ? parsed
            : SearchIntentType.Product;
        var document = await store.GetAsync(record.Id, intent, ct);
        if (document is null)
        {
            // The index row exists but its R2 document is unreadable right now — that's an upstream
            // storage gap, not a missing entity.
            return Results.Json(new { error = "document_unavailable" }, statusCode: StatusCodes.Status404NotFound);
        }

        // Serialize with the document's own options (camelCase, enums as strings) — the same shape
        // the R2 object itself has, so API consumers and raw-R2 readers see one format.
        return Results.Json(document, SearchEntityStore.DocumentJson);
    }

    // ── /stores ───────────────────────────────────────────────────────────────

    internal static async Task<IResult> ListStoresAsync(
        IStoreRepository stores,
        string? q = null, string? location = null, string? type = null, int page = 1,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var rows = await stores.SearchAsync(q, (page - 1) * PageSize, PageSize, location, type, ct);
        return Results.Json(new
        {
            page,
            pageSize = PageSize,
            count = rows.Count,
            stores = rows.Select(StoreProfileDto.From)
        });
    }

    internal static async Task<IResult> GetStoreAsync(
        int id, IStoreRepository stores, CancellationToken ct = default)
    {
        var row = await stores.GetByIdAsync(id, ct);
        return row is null
            ? Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound)
            : Results.Json(StoreProfileDto.From(row));
    }

    // ── /brands ───────────────────────────────────────────────────────────────

    internal static async Task<IResult> ListBrandsAsync(
        IBrandRepository brands,
        string? q = null, string? category = null, int page = 1,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var rows = await brands.SearchAsync(q, (page - 1) * PageSize, PageSize, category, ct);
        return Results.Json(new
        {
            page,
            pageSize = PageSize,
            count = rows.Count,
            brands = rows.Select(BrandProfileDto.From)
        });
    }

    internal static async Task<IResult> GetBrandAsync(
        int id, IBrandRepository brands, CancellationToken ct = default)
    {
        var row = await brands.GetByIdAsync(id, ct);
        return row is null
            ? Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound)
            : Results.Json(BrandProfileDto.From(row));
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────
    // Explicit response shapes (never the EF entities): the API contract must not silently grow a
    // field because a column was added, and the index row's internal bookkeeping (NameKey, R2Key…)
    // is not part of the product.

    /// <summary>One entity-index row on the /items list — enough to decide whether to pay for the full doc.</summary>
    internal sealed record ItemSummary(
        string Id, string Name, string Intent, string? Geo, string? Category,
        int? BrandId, int? StoreId, DateTimeOffset LastRefreshed)
    {
        public static ItemSummary From(EntityRecord r) =>
            new(r.Id, r.Name, r.Intent, r.Geo, r.Category, r.BrandId, r.StoreId, r.LastRefreshed);
    }

    internal sealed record StoreProfileDto(
        int Id, string Name, string? Location, string? Type, string? Website,
        IReadOnlyList<string> BrandsCarried, double? Rating,
        string? Phone, string? Email, string? Address,
        double? Latitude, double? Longitude, IReadOnlyList<string> OpeningHours,
        double? GoogleRating, int? GoogleReviewCount, string? GoogleMapsUrl,
        bool IsVerified, DateTimeOffset LastRefreshed)
    {
        public static StoreProfileDto From(Store s) => new(
            s.Id, s.Name, s.Location, s.Type, s.Website, s.BrandsCarried, s.Rating,
            s.Phone, s.Email, s.Address, s.Latitude, s.Longitude, s.OpeningHours,
            s.GoogleRating, s.GoogleReviewCount, s.GoogleMapsUrl, s.IsVerified, s.LastRefreshed);
    }

    internal sealed record BrandProfileDto(
        int Id, string Name, string? CountryOfOrigin, double? ReputationScore, string? Description,
        IReadOnlyList<string> Pros, IReadOnlyList<string> Cons, IReadOnlyList<string> PopularModels,
        string? PriceRange, string? Website, IReadOnlyList<string> SocialLinks,
        DateTimeOffset LastRefreshed)
    {
        public static BrandProfileDto From(Brand b) => new(
            b.Id, b.Name, b.CountryOfOrigin, b.ReputationScore, b.Description,
            b.Pros, b.Cons, b.PopularModels, b.PriceRange, b.Website, b.SocialLinks,
            b.LastRefreshed);
    }
}
