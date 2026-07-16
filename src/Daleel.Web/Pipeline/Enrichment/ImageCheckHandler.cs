using Daleel.Core.Models;
using Daleel.Core.Moderation;
using Daleel.Web.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// HALAL SAFETY pass over the FINAL grid images — the fail-CLOSED gate the UI depends on. Product/brand
/// images are HIDDEN by default (the UI renders <c>DisplayImageUrl</c>, which is null until this unit
/// PROMOTES its <c>VerifiedImages</c>); it vision-screens every candidate photo of every item and promotes ONLY
/// the ones judged clean, leaving flagged (immodest/alcohol/pork…) AND could-not-screen images hidden.
/// Nothing is destructively stripped — the raw URL is kept, so a later admin whitelist or a retry can
/// un-hide it. On a screen that could NOT run (OpenRouter 402 out-of-credits / provider outage) the unit
/// REQUEUES: it stays queued and the images stay hidden until the screen can actually run — never showing
/// an unverified image. A missing vision model (not configured) is moderation-off and passes through.
/// </summary>
public sealed class ImageCheckHandler : IEnrichmentUnitHandler
{
    /// <summary>Distinct images screened per pass. Generous — anything beyond stays hidden (fail-closed).</summary>
    private const int MaxImages = 200;

    /// <summary>Backoff between requeues while the vision screen is unavailable (billing/infra outage).</summary>
    private static readonly TimeSpan InfraBackoff = TimeSpan.FromMinutes(5);

    private readonly ILogger<ImageCheckHandler> _logger;

    public ImageCheckHandler(ILogger<ImageCheckHandler> logger) => _logger = logger;

    public string Kind => EnrichmentUnit.ImageCheck;

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        var classifier = ctx.Services.GetService<IHalalImageClassifier>();

        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products: { Models.Count: > 0 } products })
        {
            return UnitOutcome.Ok;
        }

        // Screen EVERY candidate photo of every item (the whole gallery), plus brand logos.
        var urls = products.Models.SelectMany(m => m.CandidateImages)
            .Concat(products.Brands.Select(b => b.LogoUrl).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxImages)
            .ToList();
        if (urls.Count == 0)
        {
            return UnitOutcome.Ok;
        }

        var attempted = new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);
        var flaggedVerdicts = new Dictionary<string, ImageVerdict>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> flagged;
        HashSet<string> unscreened;
        var configured = classifier is { IsConfigured: true };

        if (!configured)
        {
            // No vision model = moderation intentionally OFF (a deployment choice, not a failure): VERIFY
            // (show) every image. Fail-closed hiding is scoped to a configured screen that can't RUN, never
            // to "no screen at all" — otherwise a missing key would blank the whole app.
            flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            unscreened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            ImageClassifierResult screen;
            try
            {
                screen = await classifier!.ClassifyAsync(urls, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new UnitOutcome.Retry($"image screen failed: {ex.Message}");
            }

            // Keep the FULL flagged verdicts (category/score/reason) for the audit log, not just the URLs.
            foreach (var v in screen.Flagged.Where(v => !string.IsNullOrWhiteSpace(v.ImageUrl)))
            {
                flaggedVerdicts[v.ImageUrl] = v;
            }
            flagged = flaggedVerdicts.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            unscreened = new HashSet<string>(screen.Unscreened, StringComparer.OrdinalIgnoreCase);
        }

        // PRODUCT-SHOT screen: of the images that would display (passed the halal screen), drop the ones
        // that are not a clean product photo — lifestyle/room scenes, promo banners, logos, collages. This
        // is FAIL-OPEN (unlike the halal screen): a screen outage rejects nothing, so it only ever removes
        // a bad photo, never hides a good one it couldn't judge, and never requeues the unit.
        IReadOnlySet<string> notProductShot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var productScreen = ctx.Services.GetService<Daleel.Web.Moderation.IProductImageScreen>();
        if (productScreen is { IsConfigured: true })
        {
            var wouldShow = urls.Where(u => !flagged.Contains(u) && !unscreened.Contains(u)).ToList();
            if (wouldShow.Count > 0)
            {
                try
                {
                    notProductShot = await productScreen.RejectNonProductShotsAsync(wouldShow, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Product-image screen failed for job {JobId}; keeping images", item.SearchJobId);
                }
            }
        }

        // VERIFIED (promotable) = we actually screened it AND it was neither flagged nor left unscreened,
        // AND it is a clean product shot. Everything else stays HIDDEN (fail-closed for halal): flagged,
        // could-not-screen, non-product-shot, and any image beyond the cap or non-http (never `attempted`).
        bool Verified(string? url) =>
            url is { Length: > 0 } u && attempted.Contains(u) && !flagged.Contains(u)
            && !unscreened.Contains(u) && !notProductShot.Contains(u);

        // Promote the clean photos of each item's gallery, un-promote the rest. Never nulls the raw
        // candidates — hiding is entirely via VerifiedImages, so a whitelist/retry can un-hide. Idempotent.
        await ctx.Results.PatchAsync(item, answer =>
        {
            if (answer.Products is not { } p)
            {
                return null;
            }

            var changed = false;
            var models = p.Models.Select(m =>
            {
                var verified = m.CandidateImages.Where(Verified).ToList();
                if (!verified.SequenceEqual(m.VerifiedImages, StringComparer.Ordinal))
                {
                    changed = true;
                    return m with { VerifiedImages = verified };
                }
                return m;
            }).ToList();
            var brands = p.Brands.Select(b =>
            {
                var target = Verified(b.LogoUrl) ? b.LogoUrl : null;
                if (!string.Equals(target, b.VerifiedLogoUrl, StringComparison.Ordinal))
                {
                    changed = true;
                    return b with { VerifiedLogoUrl = target };
                }
                return b;
            }).ToList();

            return changed ? answer with { Products = p with { Models = models, Brands = brands } } : null;
        }, ct);

        // AUDIT: record every candidate image's verdict for the admin /admin/images page — the decision
        // (shown/hidden/unscreened) and, when hidden, the category/score/reason. Best-effort.
        await RecordAuditAsync(ctx, item, products, attempted, flaggedVerdicts, unscreened, configured, ct);

        // FAIL-CLOSED / STAY-QUEUED: if the screen could not run for some images (billing/infra), they
        // stay hidden and the unit REQUEUES (no attempt consumed) so it re-screens once the outage clears.
        if (unscreened.Count > 0)
        {
            _logger.LogWarning(
                "Image check job {JobId}: {Unscreened} image(s) could not be screened (vision unavailable) — " +
                "held hidden and re-queued; {Flagged} flagged, {Clean} verified.",
                item.SearchJobId, unscreened.Count, flagged.Count, urls.Count - flagged.Count - unscreened.Count);
            return new UnitOutcome.Requeue("vision screen unavailable — images held until it recovers", InfraBackoff);
        }

        _logger.LogInformation(
            "Image check job {JobId}: {Clean} image(s) verified, {Flagged} hidden as haram.",
            item.SearchJobId, urls.Count - flagged.Count, flagged.Count);
        return UnitOutcome.Ok;
    }

    /// <summary>
    /// Writes one <see cref="ImageModerationLog"/> row per screened image (deduped by URL, first item
    /// wins) so the admin audit page shows every photo, its decision, and — when hidden — the vision
    /// model's category/score/reason. Best-effort: an audit-write failure never faults the screen.
    /// </summary>
    private async Task RecordAuditAsync(
        EnrichmentUnitContext ctx, EnrichmentWorkItem item, ProductSearchResult products,
        HashSet<string> attempted, IReadOnlyDictionary<string, ImageVerdict> flaggedVerdicts,
        HashSet<string> unscreened, bool configured, CancellationToken ct)
    {
        var repo = ctx.Services.GetService<IImageModerationLogRepository>();
        if (repo is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var source = configured ? "vision" : "not-configured";
        var rows = new List<ImageModerationLog>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? url, string? itemName, string kind)
        {
            if (string.IsNullOrWhiteSpace(url) || !attempted.Contains(url!) || !seen.Add(url!))
            {
                return;
            }

            string decision;
            string? category = null;
            double? score = null;
            string? reason = null;
            if (flaggedVerdicts.TryGetValue(url!, out var v))
            {
                decision = ImageModerationDecision.Hidden;
                category = v.Category;
                score = v.Confidence;
                reason = v.Reason;
            }
            else if (unscreened.Contains(url!))
            {
                decision = ImageModerationDecision.Unscreened;
            }
            else
            {
                decision = ImageModerationDecision.Shown;
            }

            rows.Add(new ImageModerationLog
            {
                SearchJobId = item.SearchJobId,
                Query = ctx.Job.Query,
                Geo = ctx.Job.Geo,
                ImageUrl = url!,
                ItemName = itemName,
                ItemKind = kind,
                Decision = decision,
                Category = category,
                Score = score,
                Reason = reason,
                DecisionSource = source,
                CreatedAt = now,
            });
        }

        foreach (var m in products.Models)
        {
            foreach (var img in m.CandidateImages)
            {
                Add(img, m.Name, "product");
            }
        }
        foreach (var b in products.Brands)
        {
            Add(b.LogoUrl, b.Name, "brand-logo");
        }

        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            await repo.RecordAsync(rows, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Image audit log write failed for job {JobId}", item.SearchJobId);
        }
    }
}
