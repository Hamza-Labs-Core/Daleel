using Daleel.Core.Moderation;
using Daleel.Web.Services;

namespace Daleel.Web.Moderation;

/// <summary>
/// The filter-worker A/B collector (architecture doc §6 Phase 3): decorates the AUTHORITATIVE
/// classifier and, per batch, fires a detached shadow call to the edge filter host, logging
/// agreement/divergence between the two. The inner classifier's verdict is ALWAYS what the
/// moderation pipeline uses — the shadow adds zero latency (detached), zero failure surface
/// (swallowed), and zero policy influence. Its output is the labeled comparison data the doc
/// requires before any default routing flips; the edge call itself is metered via the gateway.
/// </summary>
public sealed class ShadowHalalClassifier : IHalalClassifier
{
    private readonly IHalalClassifier _inner;
    private readonly IProviderApi _api;
    private readonly ILogger _logger;

    public ShadowHalalClassifier(IHalalClassifier inner, IProviderApi api, ILogger logger)
    {
        _inner = inner;
        _api = api;
        _logger = logger;
    }

    public bool IsConfigured => _inner.IsConfigured;

    public async Task<HalalClassifierResult> ClassifyAsync(
        IReadOnlyList<HalalCandidate> items, CancellationToken ct = default)
    {
        var result = await _inner.ClassifyAsync(items, ct).ConfigureAwait(false);

        if (_api.HasEdgeFilter && items.Count > 0)
        {
            // Detached: the shadow must never slow or fail the moderation path. The ambient
            // per-job observer flows into Task.Run via ExecutionContext, so the edge call still
            // meters against the originating job.
            _ = Task.Run(() => ShadowCompareAsync(items, result));
        }

        return result;
    }

    private async Task ShadowCompareAsync(IReadOnlyList<HalalCandidate> items, HalalClassifierResult authoritative)
    {
        try
        {
            var findings = await _api.FilterTextFindingsAsync(
                items.Select(i => (i.Id.ToString(), i.Text, (string?)null)).ToList(),
                CancellationToken.None).ConfigureAwait(false);

            var innerHaram = authoritative.Verdicts.Where(v => v.IsHaram).Select(v => v.Id).ToHashSet();
            var edgeHaram = findings
                .Select(f => int.TryParse(f.Id, out var id) ? id : -1)
                .Where(id => id >= 0)
                .ToHashSet();

            var agree = innerHaram.Intersect(edgeHaram).Count();
            var innerOnly = innerHaram.Except(edgeHaram).ToList();
            var edgeOnly = edgeHaram.Except(innerHaram).ToList();

            // One grep-able line per batch is the A/B dataset: aggregate agreement over time decides
            // whether the edge classifier can take default traffic (never flipped from here).
            _logger.LogInformation(
                "halal-shadow batch={Batch} innerHaram={Inner} edgeHaram={Edge} agree={Agree} " +
                "innerOnly=[{InnerOnly}] edgeOnly=[{EdgeOnly}]",
                items.Count, innerHaram.Count, edgeHaram.Count, agree,
                string.Join(',', innerOnly), string.Join(',', edgeOnly));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "halal-shadow comparison failed (best-effort)");
        }
    }
}

/// <summary>
/// Vision counterpart of <see cref="ShadowHalalClassifier"/>: shadows the authoritative image
/// classifier with the edge vision filter, logging agreement. Same contract — detached, swallowed,
/// zero policy influence.
/// </summary>
public sealed class ShadowHalalImageClassifier : IHalalImageClassifier
{
    private readonly IHalalImageClassifier _inner;
    private readonly IProviderApi _api;
    private readonly ILogger _logger;

    public ShadowHalalImageClassifier(IHalalImageClassifier inner, IProviderApi api, ILogger logger)
    {
        _inner = inner;
        _api = api;
        _logger = logger;
    }

    public bool IsConfigured => _inner.IsConfigured;

    public async Task<ImageClassifierResult> ClassifyAsync(
        IReadOnlyList<string> imageUrls, CancellationToken ct = default)
    {
        var result = await _inner.ClassifyAsync(imageUrls, ct).ConfigureAwait(false);

        if (_api.HasEdgeFilter && imageUrls.Count > 0)
        {
            _ = Task.Run(() => ShadowCompareAsync(imageUrls, result.Flagged));
        }

        return result;
    }

    private async Task ShadowCompareAsync(IReadOnlyList<string> urls, IReadOnlyList<ImageVerdict> authoritative)
    {
        try
        {
            var findings = await _api.FilterImageFindingsAsync(urls, CancellationToken.None).ConfigureAwait(false);

            var innerHaram = authoritative.Where(v => v.IsHaram).Select(v => v.ImageUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var edgeHaram = findings.Select(f => f.Url).Where(u => u is not null).Select(u => u!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation(
                "halal-shadow-vision batch={Batch} innerHaram={Inner} edgeHaram={Edge} agree={Agree}",
                urls.Count, innerHaram.Count, edgeHaram.Count, innerHaram.Intersect(edgeHaram).Count());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "halal-shadow-vision comparison failed (best-effort)");
        }
    }
}
