using System.Text.Json;
using Daleel.Core.Caching;
using Daleel.Core.Moderation;
using Daleel.Core.Observability;
using Daleel.Web.Data;
using Daleel.Web.Moderation;
using Daleel.Web.Services;

namespace Daleel.Web.Conversation;

/// <summary>The outcome of running a search job.</summary>
/// <remarks>
/// <see cref="ApiCalls"/>, <see cref="EstimatedCost"/>, <see cref="ResultCount"/> and
/// <see cref="Providers"/> are operational telemetry: recorded for analytics/cost optimisation,
/// never shown to the user.
/// </remarks>
public sealed record SearchRunResult(
    string ResultJson, string ResultType, int FilteredCount, string FilteredCategories,
    int ApiCalls = 0, decimal EstimatedCost = 0m, int ResultCount = 0, string Providers = "",
    int Credits = 0)
{
    /// <summary>
    /// The completeness verdict when this result was served from cache (null on a fresh run). A
    /// <see cref="CacheDecision.ServeAndEnrich"/> verdict tells the worker to run a background partial
    /// re-enrichment for just the missing pieces; <see cref="CacheDecision.ServeAsIs"/> means the hit was
    /// complete and needs no follow-up.
    /// </summary>
    public Daleel.Web.Pipeline.CacheQualityReport? CacheQuality { get; init; }

    /// <summary>
    /// True when the run was cut short by the per-job cost cap (the result, if any, was salvaged).
    /// The worker must not launch the post-result background enrichment for such a job — a fresh
    /// enrichment budget would double the admin-configured spending ceiling exactly when it fired.
    /// </summary>
    public bool CostCapTripped { get; init; }
}

/// <summary>
/// Runs the actual agent query for a job. Abstracted from <c>SearchJobService</c> so the worker can
/// be unit-tested with a fake runner (no real LLM/providers needed).
/// </summary>
public interface ISearchRunner
{
    Task<SearchRunResult> RunAsync(SearchJob job, Action<string> progress, CancellationToken ct);

    // Full post-result enrichment is no longer a runner concern: it fans out through the durable
    // enrichment work queue (Pipeline/Enrichment), one unit per API dive, each saved as it lands.
    // Only the smart-cache gap refill below remains here, wrapped by the queue's CacheGapRefill unit.

    /// <summary>
    /// Partial re-enrichment of a cache hit that scored below the full-quality bar: re-scrapes only the
    /// pieces the <paramref name="report"/> flagged as missing (thin product specs/prices/images, brands
    /// without a logo/description, unverified stores), updates the cache, and returns the refreshed result
    /// to stream to the UI — or null when nothing could be improved. Default is a no-op so legacy/test
    /// runners opt out.
    /// </summary>
    Task<SearchRunResult?> ReEnrichAsync(
        SearchJob job, SearchRunResult baseResult, Daleel.Web.Pipeline.CacheQualityReport report,
        Action<string> progress, CancellationToken ct) =>
        Task.FromResult<SearchRunResult?>(null);
}

/// <summary>Production runner: builds an agent from server-side keys and runs the unified ask flow.</summary>
public sealed class AgentSearchRunner : ISearchRunner
{
    /// <summary>TTL for both cache layers — a cached search stays valid for 30 days.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private readonly IAgentFactory _agents;
    private readonly ISystemConfigService _config;
    private readonly IApiCallLogRepository _apiLog;
    private readonly IFilteredContentLogRepository _filteredLog;
    private readonly ICacheStore _cache;
    private readonly IModerationPolicyProvider _moderationPolicy;
    private readonly IHalalImageClassifier _imageClassifier;
    private readonly ILogger<AgentSearchRunner> _logger;

    public AgentSearchRunner(IAgentFactory agents, ISystemConfigService config, IApiCallLogRepository apiLog,
        IFilteredContentLogRepository filteredLog, ICacheStore cache,
        IModerationPolicyProvider moderationPolicy, IHalalImageClassifier imageClassifier,
        ILogger<AgentSearchRunner> logger)
    {
        _agents = agents;
        _config = config;
        _apiLog = apiLog;
        _filteredLog = filteredLog;
        _cache = cache;
        _moderationPolicy = moderationPolicy;
        _imageClassifier = imageClassifier;
        _logger = logger;
    }

    /// <summary>
    /// The cached shape of a completed search — enough to replay the report verbatim, including the
    /// content-filter stats (<see cref="FilteredCount"/>/<see cref="FilteredCategories"/>) so a cache
    /// hit reports the same moderation telemetry the original run did rather than zeroes.
    /// </summary>
    private sealed record CachedResult(
        string ResultJson, string ResultType, int FilteredCount = 0, string FilteredCategories = "");

    public async Task<SearchRunResult> RunAsync(SearchJob job, Action<string> progress, CancellationToken ct)
    {
        // Normalize the language the same way the agent does (blank/whitespace ⇒ "en") so an
        // unspecified-language search shares a cache entry with an explicit "en" one rather than
        // missing the full-result cache.
        var language = string.IsNullOrWhiteSpace(job.Language) ? "en" : job.Language;

        // Full-result cache: an identical normalized query+geo within the TTL replays the whole
        // report without running the agent at all (no LLM/provider calls). Hit or miss is logged.
        var resultKey = CacheKey.ForResult(job.Query, job.Geo, language);
        if (await TryGetResultAsync(resultKey, ct).ConfigureAwait(false) is { } cached)
        {
            progress("⚡ Loaded from cache — identical search run recently.");
            await RecordResultCacheAsync(job, "hit", ct).ConfigureAwait(false);
            return new SearchRunResult(
                cached.ResultJson, cached.ResultType, cached.FilteredCount, cached.FilteredCategories ?? "");
        }
        await RecordResultCacheAsync(job, "miss", ct).ConfigureAwait(false);

        // Per-job cost instrumentation: estimate + cap from admin config, stream each call live.
        var estimator = await CostConfig.BuildEstimatorAsync(_config, ct).ConfigureAwait(false);
        var caps = await CostConfig.ReadCapsAsync(_config, ct).ConfigureAwait(false);

        using var capTrip = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Per-call detail (provider/endpoint/cost/timing) is internal: route it to the server log,
        // not to the user's progress stream. Aggregate counts/cost still flow into analytics below.
        var collector = new JobApiCallCollector(
            line => _logger.LogInformation("Search job {JobId} API call · {Detail}", job.Id, line),
            caps.MaxPerJob, capTrip);

        // Admin feedback drives moderation: the active whitelist (undo decisions) and the
        // rating-tuned thresholds ride into the agent with every run.
        var moderation = await _moderationPolicy.GetAsync(ct).ConfigureAwait(false);

        // Background jobs resolve keys from server env only (browser BYO keys aren't available here).
        var agent = _agents.Build(new AgentRequest
        {
            Geo = job.Geo,
            Model = string.IsNullOrWhiteSpace(job.Model) ? null : job.Model,
            Language = language,
            Log = progress,
            ApiObserver = collector,
            CostEstimator = estimator,
            Cache = _cache,
            CacheTtl = CacheTtl,
            ModerationWhitelist = moderation.WhitelistKeys,
            ModerationCategories = moderation.Categories,
            HalalPolicy = moderation.Policy,
            ImageClassifier = _imageClassifier
        });

        try
        {
            var answer = await agent.AskAsync(job.Query, job.Geo, capTrip.Token).ConfigureAwait(false);

            var audit = agent.ContentFilter.AuditLog;
            var filteredCategories = string.Join(",", audit
                .Select(a => a.Contains(':') ? a[(a.IndexOf(':') + 1)..] : a)
                .Distinct());

            // Telemetry for analytics / cost optimisation (server-side only).
            var providers = string.Join(",", collector.Calls
                .Select(c => c.Provider)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            var resultCount = answer.Products?.ProductCount
                ?? (answer.Research.WebResults.Count + answer.Research.ShoppingResults.Count);

            var resultJson = ResultSerialization.Serialize(answer);

            // Cache the finished (already content-filtered) report — with its moderation stats — so
            // the next identical search replays the same FilteredCount/categories rather than zeroes.
            await TrySetResultAsync(
                resultKey, new CachedResult(resultJson, "ask", audit.Count, filteredCategories), ct).ConfigureAwait(false);

            return new SearchRunResult(
                resultJson,
                "ask",
                audit.Count,
                filteredCategories,
                collector.Calls.Count,
                collector.TotalCost,
                resultCount,
                providers,
                collector.TotalCredits);
        }
        finally
        {
            // Persist the call log regardless of outcome (success, cap-trip, or error) so usage
            // and cost are always recorded.
            await PersistAsync(job, collector, ct).ConfigureAwait(false);

            // Record what the halal filter removed for admin review (anonymous — no userId).
            await PersistFilteredAsync(job, agent.ContentFilter.AuditDetails, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Persists the filter's removals to the admin-only <see cref="FilteredContentLog"/>. Carries
    /// the query and the matched rule, but never the user id — filter review is anonymous.
    /// </summary>
    private async Task PersistFilteredAsync(
        SearchJob job, IReadOnlyList<FilterFinding> details, CancellationToken ct)
    {
        if (details.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var rows = details.Select(d => FilteredContentLog.From(d, job.Query, job.Geo, now));

        try
        {
            await _filteredLog.AddBatchAsync(rows, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: never let audit logging affect the search outcome
        }
    }

    private async Task PersistAsync(SearchJob job, JobApiCallCollector collector, CancellationToken ct)
    {
        var calls = collector.Calls;
        if (calls.Count == 0)
        {
            return;
        }

        // Cost rows are anonymised: store a one-way hash of the user id so the log can't be traced
        // to an account. The user's own usage view hashes the same way to find their rows.
        var hashedUser = Anonymizer.HashUserId(job.UserId);
        var rows = calls.Select(c => new ApiCallLog
        {
            UserId = hashedUser,
            JobId = job.Id,
            Provider = c.Provider,
            Endpoint = c.Endpoint,
            RequestSummary = c.RequestSummary,
            ResponseTimeMs = c.ResponseTimeMs,
            ResponseBytes = c.ResponseBytes,
            Status = c.Status.ToString().ToLowerInvariant(),
            EstimatedCost = c.EstimatedCost,
            Model = c.Model,
            InputTokens = c.InputTokens,
            OutputTokens = c.OutputTokens,
            CreatedAt = c.Timestamp
        });

        // Don't let a logging failure mask the job's real outcome; and persist even if the job was cancelled.
        try
        {
            await _apiLog.AddBatchAsync(rows, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Reads + deserializes the full-result cache. Any failure is treated as a miss.</summary>
    private async Task<CachedResult?> TryGetResultAsync(string key, CancellationToken ct)
    {
        try
        {
            var payload = await _cache.GetAsync(key, ct).ConfigureAwait(false);
            return payload is null ? null : JsonSerializer.Deserialize<CachedResult>(payload);
        }
        catch
        {
            return null; // cache/db hiccup or corrupt payload ⇒ run the search live
        }
    }

    /// <summary>Stores the finished report under the result key. Best-effort; never fails the search.</summary>
    private async Task TrySetResultAsync(string key, CachedResult value, CancellationToken ct)
    {
        try
        {
            await _cache.SetAsync(key, JsonSerializer.Serialize(value), CacheTtl, ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Records a result-layer cache hit/miss to <see cref="ApiCallLog"/> (Provider "cache").</summary>
    private async Task RecordResultCacheAsync(SearchJob job, string outcome, CancellationToken ct)
    {
        var row = new ApiCallLog
        {
            UserId = Anonymizer.HashUserId(job.UserId),
            JobId = job.Id,
            Provider = "cache",
            Endpoint = $"result/{outcome}",
            RequestSummary = RequestSummaries.Truncate(job.Query),
            Status = "success",
            EstimatedCost = 0m,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _apiLog.AddBatchAsync(new[] { row }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: cache telemetry must never affect the search outcome
        }
    }
}
