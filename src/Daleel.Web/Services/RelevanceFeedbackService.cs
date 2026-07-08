using Daleel.Core.Models;
using Daleel.Pipeline.Extraction;
using Daleel.Web.Data;
using Daleel.Web.Events;

namespace Daleel.Web.Services;

/// <summary>Records a user's "not relevant" flag on a result item — the capture half of the learning loop.</summary>
public interface IRelevanceFeedbackService
{
    Task RecordAsync(
        ProductModel model, string query, string? target, string? geo, string? reason, string? userId,
        CancellationToken ct = default);
}

/// <summary>
/// Turns a flagged <see cref="ProductModel"/> into a <see cref="RelevanceFlag"/> keyed by the SAME identity
/// the grid uses — <see cref="ListingExtractor.DedupKey"/> + the model's routing id — so the learning loop
/// can match future items. The DB insert is the real capture; the timeline event is best-effort.
/// </summary>
public sealed class RelevanceFeedbackService : IRelevanceFeedbackService
{
    private readonly IRelevanceFlagRepository _repo;
    private readonly ISystemEventLog _log;

    public RelevanceFeedbackService(IRelevanceFlagRepository repo, ISystemEventLog log)
        => (_repo, _log) = (repo, log);

    public async Task RecordAsync(
        ProductModel model, string query, string? target, string? geo, string? reason, string? userId,
        CancellationToken ct = default)
    {
        // Capture requires an identity (so it can be deduped/attributed) and a query (the thing judged).
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        // Key the flag exactly as the grid keys the item (DedupKey), so the loop matches future items.
        var dedupKey = ListingExtractor.DedupKey(new ProductListing
        {
            Brand = model.Brand,
            Model = model.Model,
            Name = model.Name
        });

        var flag = new RelevanceFlag
        {
            UserHash = Anonymizer.HashUserId(userId),
            Query = query.Trim(),
            QueryKey = RelevanceFlag.QueryKeyOf(query),
            Target = string.IsNullOrWhiteSpace(target) ? null : target.Trim(),
            Geo = string.IsNullOrWhiteSpace(geo) ? null : geo,
            DedupKey = dedupKey,
            StableId = model.Id,
            Brand = model.Brand,
            Model = model.Model,
            Name = model.Name,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _repo.AddAsync(flag, ct).ConfigureAwait(false);

        if (_log.IsEnabled)
        {
            await _log.LogAsync(
                SystemEventCategory.Item, "relevance.flagged",
                $"Flagged not relevant: {model.Name} for \"{query}\"",
                source: "feedback", userHash: flag.UserHash,
                details: new Dictionary<string, object?>
                {
                    ["dedupKey"] = dedupKey,
                    ["query"] = query,
                    ["target"] = target
                },
                ct: ct).ConfigureAwait(false);
        }
    }
}
