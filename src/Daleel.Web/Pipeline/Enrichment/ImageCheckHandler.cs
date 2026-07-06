using Daleel.Core.Moderation;
using Daleel.Web.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// HALAL SAFETY pass over the FINAL grid images. The gather-stage vision moderation only screens the
/// raw web/shopping search results — but the images the user actually sees are assigned later by
/// extraction, the paid image-lookup, catalogue crawls and the brand DB, and were NEVER re-screened.
/// This unit runs after those settle: it vision-classifies every product/brand image still on the
/// answer and STRIPS any judged haram (immodest/indecent, alcohol, pork…), keeping the item but
/// removing its image. Always runs (no flag) — a missing vision model simply no-ops. Best-effort:
/// a failed vision call retries; a failure never leaves a haram image on-screen silently forever
/// because the unit re-leases and re-runs.
/// </summary>
public sealed class ImageCheckHandler : IEnrichmentUnitHandler
{
    /// <summary>Distinct images screened per pass — bounded so one search can't run an unbounded vision bill.</summary>
    private const int MaxImages = 60;

    private readonly ILogger<ImageCheckHandler> _logger;

    public ImageCheckHandler(ILogger<ImageCheckHandler> logger) => _logger = logger;

    public string Kind => EnrichmentUnit.ImageCheck;

    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        var classifier = ctx.Services.GetService<IHalalImageClassifier>();
        if (classifier is null || !classifier.IsConfigured)
        {
            return UnitOutcome.Ok; // no vision model configured — nothing to screen
        }

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

        IReadOnlyList<ImageVerdict> verdicts;
        try
        {
            // ClassifyAsync returns verdicts ONLY for images judged haram (empty on failure/none).
            verdicts = await classifier.ClassifyAsync(urls, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new UnitOutcome.Retry($"image screen failed: {ex.Message}");
        }

        var haram = verdicts
            .Where(v => v.IsHaram && !string.IsNullOrWhiteSpace(v.ImageUrl))
            .Select(v => v.ImageUrl)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (haram.Count == 0)
        {
            return UnitOutcome.Ok;
        }

        // Strip the haram IMAGE, keep the item (per the halal invariant: an image flag never removes
        // the product, only its picture). Additive/idempotent — a re-run over already-stripped images
        // finds nothing to change.
        await ctx.Results.PatchAsync(item, answer =>
        {
            if (answer.Products is not { } p)
            {
                return null;
            }

            var changed = false;
            var models = p.Models.Select(m =>
            {
                if (m.ImageUrl is { } u && haram.Contains(u)) { changed = true; return m with { ImageUrl = null }; }
                return m;
            }).ToList();
            var brands = p.Brands.Select(b =>
            {
                if (b.LogoUrl is { } u && haram.Contains(u)) { changed = true; return b with { LogoUrl = null }; }
                return b;
            }).ToList();

            return changed ? answer with { Products = p with { Models = models, Brands = brands } } : null;
        }, ct);

        _logger.LogInformation(
            "Image check job {JobId}: stripped {Count} haram grid image(s)", item.SearchJobId, haram.Count);
        return UnitOutcome.Ok;
    }
}
