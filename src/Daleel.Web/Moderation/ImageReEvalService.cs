using Daleel.Core.Moderation;
using Daleel.Web.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Moderation;

/// <summary>
/// Drains the image re-evaluation QUEUE: images an admin flagged on /admin/images (their
/// <see cref="ImageModerationLog.ReEvalRequestedAt"/> is non-null) are re-screened against the CURRENT
/// rule list — bypassing the verdict cache so a rule change actually re-judges them — and their registry
/// verdict is updated. Runs periodically like the other moderation background services. Fail-closed:
/// an image the screen COULD NOT run on (billing/infra) is left queued and retried next cycle.
/// </summary>
public sealed class ImageReEvalService : BackgroundService
{
    /// <summary>Images re-screened per cycle (the classifier batches these internally).</summary>
    private const int BatchSize = 30;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHalalImageClassifier _classifier;
    private readonly ILogger<ImageReEvalService> _logger;

    public ImageReEvalService(
        IServiceScopeFactory scopeFactory, IHalalImageClassifier classifier, ILogger<ImageReEvalService> logger)
    {
        _scopeFactory = scopeFactory;
        _classifier = classifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Keep draining while a FULL batch was fully cleared (real progress), so a big
                // "re-evaluate all" drains without waiting an interval per batch. Stops as soon as a
                // batch is partial or leaves any row queued (e.g. all-unscreened on an outage) — never
                // re-claiming stuck rows in a tight loop; those retry on the next interval.
                while (await DrainOnceAsync(stoppingToken).ConfigureAwait(false) == BatchSize)
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Image re-evaluation cycle failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Processes one batch of flagged images. Returns how many were claimed.</summary>
    private async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IImageModerationLogRepository>();

        var batch = await repo.ClaimReEvalBatchAsync(BatchSize, ct).ConfigureAwait(false);
        if (batch.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;

        // No vision model = moderation intentionally off → show all; clear the queue.
        if (!_classifier.IsConfigured)
        {
            foreach (var row in batch)
            {
                await repo.ApplyReEvalVerdictAsync(
                    row.Id, ImageModerationDecision.Shown, null, null, null, "not-configured", now, ct)
                    .ConfigureAwait(false);
            }
            return batch.Count;
        }

        var urls = batch.Select(b => b.ImageUrl).ToList();
        ImageClassifierResult screen;
        try
        {
            screen = await _classifier.ClassifyAsync(urls, ct, bypassCache: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The whole screen failed — leave the batch queued and retry next cycle (fail-closed).
            _logger.LogWarning(ex, "Re-evaluation screen failed for {Count} image(s); left queued", urls.Count);
            return 0;
        }

        var flagged = screen.Flagged
            .Where(v => !string.IsNullOrWhiteSpace(v.ImageUrl))
            .GroupBy(v => v.ImageUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var unscreened = new HashSet<string>(screen.Unscreened, StringComparer.OrdinalIgnoreCase);

        var applied = 0;
        foreach (var row in batch)
        {
            if (flagged.TryGetValue(row.ImageUrl, out var v))
            {
                await repo.ApplyReEvalVerdictAsync(
                    row.Id, ImageModerationDecision.Hidden, v.Category, v.Confidence, v.Reason, "vision", now, ct)
                    .ConfigureAwait(false);
            }
            else if (unscreened.Contains(row.ImageUrl))
            {
                // Could not screen this one (infra) — keep it queued to retry, but move it to the BACK of
                // the queue so a wall of un-screenable images can't starve newer flagged rows.
                await repo.RequeueReEvalAsync(row.Id, ct).ConfigureAwait(false);
                continue;
            }
            else
            {
                await repo.ApplyReEvalVerdictAsync(
                    row.Id, ImageModerationDecision.Shown, null, null, null, "vision", now, ct)
                    .ConfigureAwait(false);
            }
            applied++;
        }

        _logger.LogInformation(
            "Image re-evaluation: {Applied}/{Claimed} images re-screened ({Hidden} hidden).",
            applied, batch.Count, flagged.Count);
        // Return the number CLEARED (not claimed): the drain loop continues only on a full cleared batch,
        // so a batch left queued (unscreened/outage) stops the loop instead of re-claiming the same rows.
        return applied;
    }
}
