using Daleel.Core.Models;
using Daleel.Core.Pipeline;

namespace Daleel.Pipeline;

/// <summary>
/// The end-to-end orchestrator. For each source it fetches posts, matches them against
/// the job's keywords using the Arabic matcher, removes duplicates, and writes the hits.
/// </summary>
/// <remarks>
/// Flow per source:
/// <code>
/// Fetch posts (IPostFetcher)
///   → Match keywords (IPostMatcher, Arabic-normalized)
///   → Dedup (PostDeduplicator, content hash)
///   → Write results (IResultWriter, JSONL)
/// </code>
/// Dependencies are injected so the pipeline can be unit-tested with a fake fetcher and
/// an in-memory writer while still exercising the <em>real</em> Arabic matcher.
/// </remarks>
public class MonitoringPipeline : IPipeline
{
    private readonly IPostFetcher _fetcher;
    private readonly IPostMatcher _matcher;
    private readonly IResultWriter _writer;
    private readonly Action<string>? _log;

    public MonitoringPipeline(
        IPostFetcher fetcher,
        IPostMatcher matcher,
        IResultWriter writer,
        Action<string>? log = null)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _log = log;
    }

    public async Task<PipelineReport> RunAsync(MonitoringJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // One deduplicator spans the whole run so duplicates are caught across sources,
        // not just within a single source's batch.
        var deduplicator = new PostDeduplicator();

        var sourcesProcessed = 0;
        var postsFetched = 0;
        var duplicates = 0;
        var matches = 0;

        foreach (var source in job.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Seed search-style sources with the first keyword when the source has no
            // explicit target text of its own.
            var seedKeyword = source.Kind == SourceKind.Search && string.IsNullOrEmpty(source.Target)
                ? job.Keywords.FirstOrDefault()
                : null;

            _log?.Invoke($"Fetching from source '{source.Name}' ({source.Kind})…");
            var posts = await _fetcher.FetchAsync(source, seedKeyword, cancellationToken).ConfigureAwait(false);
            sourcesProcessed++;
            postsFetched += posts.Count;

            foreach (var post in posts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var match = _matcher.Match(post.Text, job.Keywords, job.Mode, job.FuzzyThreshold);
                if (!match.IsMatch)
                {
                    continue;
                }

                // Only dedupe posts we actually care about — keeps the hash set small
                // and means non-matching noise never crowds out real hits.
                if (!deduplicator.IsUnique(post))
                {
                    duplicates++;
                    continue;
                }

                await _writer.WriteAsync(
                    new MatchedPost { Post = post, Match = match },
                    cancellationToken).ConfigureAwait(false);
                matches++;
            }
        }

        var report = new PipelineReport
        {
            SourcesProcessed = sourcesProcessed,
            PostsFetched = postsFetched,
            Duplicates = duplicates,
            Matches = matches
        };

        _log?.Invoke(
            $"Done. sources={report.SourcesProcessed} fetched={report.PostsFetched} " +
            $"matches={report.Matches} duplicates={report.Duplicates}");

        return report;
    }
}
