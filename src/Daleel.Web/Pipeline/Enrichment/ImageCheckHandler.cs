using Daleel.Core.Moderation;
using Daleel.Web.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// HALAL SAFETY pass over the FINAL grid images — the fail-CLOSED gate the UI depends on. Product/brand
/// images are HIDDEN by default (the UI renders <c>DisplayImageUrl</c>, which is null until this unit
/// PROMOTES <c>VerifiedImageUrl</c>); it vision-screens every image the user would see and promotes ONLY
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

        var urls = products.Models.Select(m => m.ImageUrl)
            .Concat(products.Brands.Select(b => b.LogoUrl))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxImages)
            .ToList();
        if (urls.Count == 0)
        {
            return UnitOutcome.Ok;
        }

        var attempted = new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);
        HashSet<string> flagged;
        HashSet<string> unscreened;

        if (classifier is null || !classifier.IsConfigured)
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
                screen = await classifier.ClassifyAsync(urls, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new UnitOutcome.Retry($"image screen failed: {ex.Message}");
            }

            flagged = screen.Flagged
                .Where(v => !string.IsNullOrWhiteSpace(v.ImageUrl))
                .Select(v => v.ImageUrl)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            unscreened = new HashSet<string>(screen.Unscreened, StringComparer.OrdinalIgnoreCase);
        }

        // VERIFIED (promotable) = we actually screened it AND it was neither flagged nor left unscreened.
        // Everything else stays HIDDEN (fail-closed): flagged, could-not-screen, and any image beyond the
        // cap or non-http (never in `attempted`).
        bool Verified(string? url) =>
            url is { Length: > 0 } u && attempted.Contains(u) && !flagged.Contains(u) && !unscreened.Contains(u);

        // Promote clean images, un-promote everything else. Never nulls the raw URL — hiding is entirely
        // via VerifiedImageUrl, so a whitelist/retry can un-hide. Idempotent: a re-run sets the same target.
        await ctx.Results.PatchAsync(item, answer =>
        {
            if (answer.Products is not { } p)
            {
                return null;
            }

            var changed = false;
            var models = p.Models.Select(m =>
            {
                var target = Verified(m.ImageUrl) ? m.ImageUrl : null;
                if (!string.Equals(target, m.VerifiedImageUrl, StringComparison.Ordinal))
                {
                    changed = true;
                    return m with { VerifiedImageUrl = target };
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
}
