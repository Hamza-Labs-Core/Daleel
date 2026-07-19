using System.Text.Json;
using System.Text.Json.Serialization;
using Daleel.Core.Models;
using Daleel.Core.Persistence;
using Daleel.Web.Data;
using Daleel.Web.Storage;

namespace Daleel.Web.Persistence;

/// <summary>
/// Persists search-surfaced entities under the "R2 is the store, Postgres is the index" design: the rich
/// <see cref="EntityDocument"/> is written to the R2 <c>daleel-data</c> bucket as the source of truth, and
/// a thin <see cref="EntityRecord"/> index row (with the relations resolved to FKs) is upserted to point
/// at it. Reads come back from R2.
/// </summary>
public interface ISearchEntityStore
{
    /// <summary>Writes the document to R2 and upserts its Postgres index row. Returns the index row, or null on failure.</summary>
    Task<EntityRecord?> SaveAsync(EntityDocument document, CancellationToken ct = default);

    /// <summary>Best-effort bulk save; returns how many documents were persisted (index row written).</summary>
    Task<int> SaveAllAsync(IEnumerable<EntityDocument> documents, CancellationToken ct = default);

    /// <summary>Reads an entity document back from R2 (the source of truth), or null if absent/unreadable.</summary>
    Task<EntityDocument?> GetAsync(string id, SearchIntentType intent, CancellationToken ct = default);
}

public sealed class SearchEntityStore : ISearchEntityStore
{
    /// <summary>
    /// Shared options for the R2 documents: enums as strings and camelCase, so the JSON is human-readable
    /// and stable. The SAME options must be used to read the document back.
    /// </summary>
    internal static readonly JsonSerializerOptions DocumentJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IR2StorageService _r2;
    private readonly IEntityRecordRepository _index;
    private readonly IBrandRepository _brands;
    private readonly IStoreRepository _stores;
    private readonly ILogger<SearchEntityStore> _logger;

    public SearchEntityStore(
        IR2StorageService r2,
        IEntityRecordRepository index,
        IBrandRepository brands,
        IStoreRepository stores,
        ILogger<SearchEntityStore> logger)
    {
        _r2 = r2;
        _index = index;
        _brands = brands;
        _stores = stores;
        _logger = logger;
    }

    public async Task<EntityRecord?> SaveAsync(EntityDocument document, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(document.Id))
        {
            return null;
        }

        var objectKey = document.ObjectKey;

        // 1) Write the rich JSON to R2 (the source of truth). Best-effort: an R2 outage must not fail the
        //    search — we still record the index row so the relational graph exists and a later run can
        //    re-upload the document under the same deterministic key.
        string? r2Url = null;
        try
        {
            var json = JsonSerializer.Serialize(document, DocumentJson);
            r2Url = await _r2.StoreJsonAsync(json, objectKey, R2Bucket.Data, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write entity document {Key} to R2; indexing pointer only.", objectKey);
        }

        // 2) Resolve the relations to Postgres FKs (best-effort — the rows may not exist yet).
        var brandFk = await ResolveBrandFkAsync(document.Brand, ct).ConfigureAwait(false);
        var storeFk = await ResolveStoreFkAsync(document, ct).ConfigureAwait(false);

        var record = new EntityRecord
        {
            Id = document.Id,
            Intent = document.Intent.ToString(),
            Name = document.Name,
            NameKey = EntityRecord.Normalize(document.Name),
            Geo = document.Geo,
            Category = document.Category,
            SearchId = document.SearchId,
            BrandId = brandFk,
            StoreId = storeFk,
            ProductKey = document.ProductKey,
            ParentProductKey = document.ParentProductKey,
            R2Key = objectKey,
            R2Url = r2Url,
            LastRefreshed = document.CapturedAt
        };

        try
        {
            return await _index.UpsertAsync(record, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upsert entity index row {Id}.", document.Id);
            return null;
        }
    }

    public async Task<int> SaveAllAsync(IEnumerable<EntityDocument> documents, CancellationToken ct = default)
    {
        var saved = 0;
        foreach (var doc in documents)
        {
            ct.ThrowIfCancellationRequested();
            if (await SaveAsync(doc, ct).ConfigureAwait(false) is not null)
            {
                saved++;
            }
        }

        return saved;
    }

    public async Task<EntityDocument?> GetAsync(string id, SearchIntentType intent, CancellationToken ct = default)
    {
        var key = EntityDocument.KeyFor(intent, id);
        try
        {
            var obj = await _r2.ReadTextAsync(key, bucket: R2Bucket.Data, ct: ct).ConfigureAwait(false);
            return obj?.Text is { Length: > 0 } text
                ? JsonSerializer.Deserialize<EntityDocument>(text, DocumentJson)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read entity document {Key} from R2.", key);
            return null;
        }
    }

    private async Task<int?> ResolveBrandFkAsync(string? brandName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(brandName))
        {
            return null;
        }

        try
        {
            var brand = await _brands.GetByNameAsync(brandName, ct).ConfigureAwait(false);
            return brand?.Id;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<int?> ResolveStoreFkAsync(EntityDocument document, CancellationToken ct)
    {
        // Only a service/place entity maps to a single store/venue row; a product relates to many sellers.
        if (document.Intent == SearchIntentType.Product || string.IsNullOrWhiteSpace(document.Name))
        {
            return null;
        }

        try
        {
            var store = await _stores.GetByNameAsync(document.Name, ct).ConfigureAwait(false);
            return store?.Id;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
