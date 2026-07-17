using Daleel.Web.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// Re-reads an item's own detail page for a CLEAN product photo after the product-shot screen rejected
/// everything it had.
/// </summary>
/// <remarks>
/// The gap this closes: a store listing often carries only a promo banner, a marketplace logo or a
/// collage. Those land as the item's <c>ImageUrl</c>, the screen rejects them (correctly — they don't
/// show the product), nothing is left to verify, and the card renders a placeholder forever.
/// <see cref="ImageLookupHandler"/> can't rescue it, because it skips any item that HAS an
/// <c>ImageUrl</c> — and this one does; it's just unusable. The product's own page almost always has
/// the real gallery shot, so this reads it.
/// <para>
/// Additive only: the rejected image stays a candidate (hiding is entirely via <c>VerifiedImages</c>,
/// so an admin whitelist can still un-hide it), and the found photos are appended. Because the rejected
/// banner never verifies, the first CLEAN photo becomes the card's <c>DisplayImageUrl</c> — order needs
/// no fixing up.
/// </para>
/// <para>
/// Like every image source it must enqueue its own <see cref="EnrichmentUnit.ImageCheck"/>: the grid is
/// fail-closed, so an unscreened photo is an invisible photo.
/// </para>
/// </remarks>
public sealed class CleanShotHandler : IEnrichmentUnitHandler
{
    /// <summary>Detail-page fetches per execution; the continuation carries the rest (never truncated).</summary>
    private const int BatchSize = 8;

    public string Kind => EnrichmentUnit.CleanShot;

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<CleanShotPayload>(item.Payload)?.Names is not { Count: > 0 } names)
        {
            return UnitOutcome.Ok;
        }

        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products.Models: { Count: > 0 } models })
        {
            return UnitOutcome.Ok;
        }

        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var svc = ctx.Services.GetRequiredService<IItemEnrichmentService>();

        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var deferred = new List<string>();
        foreach (var model in models.Where(m => wanted.Contains(m.Name)))
        {
            // Re-check against the CURRENT grid: a later screen (or an admin whitelist) may already have
            // given this item something to render, and then there is nothing to fix.
            if (!string.IsNullOrWhiteSpace(model.DisplayImageUrl))
            {
                continue;
            }

            if (found.Count >= BatchSize)
            {
                deferred.Add(model.Name);
                continue;
            }

            var gallery = await svc.FindImageForItemAsync(ctx.Agent(), model, ct);
            var fresh = gallery
                .Where(u => !model.CandidateImages.Contains(u, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (fresh.Count > 0)
            {
                found[model.Name] = fresh;
            }
        }

        if (found.Count > 0)
        {
            await ctx.Results.PatchAsync(item, answer =>
            {
                if (answer.Products is not { } p)
                {
                    return null;
                }

                var current = p.Models.ToList();
                var changed = false;
                for (var i = 0; i < current.Count; i++)
                {
                    if (!found.TryGetValue(current[i].Name, out var fresh))
                    {
                        continue;
                    }

                    // Append, never replace: the rejected photo keeps its place as an unverified
                    // candidate so the audit trail and any later whitelist still see it.
                    var merged = current[i].Images.Concat(fresh)
                        .Where(u => !string.IsNullOrWhiteSpace(u))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (merged.Count != current[i].Images.Count)
                    {
                        current[i] = current[i] with { Images = merged };
                        changed = true;
                    }
                }

                return changed ? answer with { Products = p with { Models = current } } : null;
            }, ct);

            // Screen what just landed — until this runs the new photos are candidates, not pixels.
            await ctx.Queue.EnqueueAsync(new[]
            {
                HandlerHelpers.Child(item, EnrichmentUnit.ImageCheck, string.Empty,
                    notBefore: TimeSpan.FromSeconds(20))
            }, ct);
        }

        if (deferred.Count > 0)
        {
            // Enqueued directly (not via the screen's trigger, which is latched to once per job), so the
            // remaining items are covered without the two units ping-ponging.
            await ctx.Queue.EnqueueAsync(new[]
            {
                HandlerHelpers.Child(item, EnrichmentUnit.CleanShot,
                    EnrichmentWorkQueue.Payload(new CleanShotPayload(deferred)),
                    notBefore: TimeSpan.FromSeconds(30))
            }, ct);
        }

        return UnitOutcome.Ok;
    }
}
