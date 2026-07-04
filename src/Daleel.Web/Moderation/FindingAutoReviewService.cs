using System.Text;
using System.Text.Json.Serialization;
using Daleel.Core.Llm;
using Daleel.Core.Moderation;
using Daleel.Web.Data;
using Daleel.Web.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Daleel.Web.Moderation;

/// <summary>
/// The dynamic half of the feedback loop: an LLM auditor reviews every persisted finding after
/// the fact and the keyword rules update themselves. Verdicts land as <c>AutoRating</c> (feeding
/// the same threshold tuning as admin ratings, which always override), and when the SAME keyword
/// term keeps producing wrong flags — <see cref="SuppressionConsensus"/> halal audits, zero haram,
/// and no admin saying otherwise — a term suppression activates automatically. Term ADDITIONS the
/// auditor proposes stay "pending" for one-click admin approval: auto-adding blockers is the
/// false-positive direction, so a human stays in that path.
/// </summary>
public sealed class AutoReviewEngine
{
    /// <summary>Findings audited per cycle — one LLM call's worth.</summary>
    public const int BatchSize = 40;

    /// <summary>Wrong-flag audits required before a term suppression self-activates.</summary>
    public const int SuppressionConsensus = 3;

    private const string SystemPrompt =
        "You are auditing a halal content filter's past decisions for a Muslim shopping assistant. " +
        "For each finding you get: the category flagged, the rule or reason that triggered it, " +
        "whether the item was removed or shown, and the item's text. Judge the CONTENT itself: is " +
        "this item actually haram (it sells, serves, or promotes alcohol, pork, gambling, adult " +
        "content, drugs, or tobacco)?\n\n" +
        "Judge independently of the original decision. The trigger term appearing in the text is " +
        "NOT evidence — Arabic terms especially may be fragments of unrelated words (بار inside " +
        "الغبار 'dust', بيره inside كبيره 'big'). A dehumidifier ad is halal no matter which " +
        "letters it contains. Never flag banks, financing, or riba — financial services are out of " +
        "scope. Ordinary products from a store that also sells haram goods are halal.\n\n" +
        "When a keyword-triggered finding is clearly an unrelated-word artifact, set " +
        "\"wrong_keyword\": true so the term can be suppressed.\n\n" +
        "Respond with ONLY a JSON array, one object per finding: {\"id\": number, \"haram\": " +
        "boolean, \"confidence\": number 0-1, \"note\": short string naming what the item actually " +
        "is, \"wrong_keyword\": boolean}.";

    private readonly ILogger _logger;

    public AutoReviewEngine(ILogger logger) => _logger = logger;

    /// <summary>Runs one audit cycle. Returns how many findings were reviewed.</summary>
    public async Task<int> ReviewAsync(
        ILlmClient llm,
        IFilteredContentLogRepository logs,
        IModerationRuleRepository rules,
        IMemoryCache cache,
        CancellationToken ct = default)
    {
        var findings = await logs.ListUnreviewedAsync(BatchSize, ct).ConfigureAwait(false);
        if (findings.Count == 0)
        {
            return 0;
        }

        var text = await llm.CompleteTextAsync(SystemPrompt, BuildPrompt(findings), ct).ConfigureAwait(false);
        var verdicts = (LlmJson.Deserialize<List<AuditDto>>(text) ?? new List<AuditDto>())
            .Where(v => findings.Any(f => f.Id == v.Id))
            .GroupBy(v => v.Id)
            .ToDictionary(g => g.Key, g => g.First());

        if (verdicts.Count == 0)
        {
            // Unusable response — leave the batch unreviewed for the next cycle rather than
            // stamping neutral verdicts off garbage.
            _logger.LogWarning("Auto-review: unparseable auditor response; batch of {Count} deferred", findings.Count);
            return 0;
        }

        var rulesChanged = false;
        foreach (var finding in findings)
        {
            if (!verdicts.TryGetValue(finding.Id, out var v))
            {
                // Answered batch, skipped row: stamp neutral so it can't loop forever; stats
                // ignore a zero rating.
                await logs.ApplyAutoReviewAsync(finding.Id, 0, "auditor skipped", ct).ConfigureAwait(false);
                continue;
            }

            await logs.ApplyAutoReviewAsync(
                finding.Id, v.Haram ? 1 : -1, v.Note, ct).ConfigureAwait(false);

            // Rule learning: a keyword artifact judged halal counts toward suppressing the term.
            if (!v.Haram && v.WrongKeyword
                && finding.DecisionSource == "keyword" && !string.IsNullOrWhiteSpace(finding.Rule))
            {
                rulesChanged |= await MaybeSuppressTermAsync(rules, finding, ct).ConfigureAwait(false);
            }
        }

        if (rulesChanged)
        {
            ModerationPolicyProvider.Invalidate(cache); // next search runs on the updated rule set
        }

        return findings.Count;
    }

    private async Task<bool> MaybeSuppressTermAsync(
        IModerationRuleRepository rules, FilteredContentLog finding, CancellationToken ct)
    {
        var term = finding.Rule!.Trim();
        var (autoHalal, autoHaram, adminSaysHaram) =
            await rules.TermEvidenceAsync(finding.Category, term, ct).ConfigureAwait(false);

        // The human veto: if any admin marked a finding of this term haram, the machine may not
        // silence it. And a term that still catches real haram is doing its job.
        if (adminSaysHaram || autoHaram > 0 || autoHalal < SuppressionConsensus)
        {
            return false;
        }

        var rule = await rules.UpsertAsync(
            ModerationRule.SuppressTerm, finding.Category, term, DetectLanguage(term),
            $"auto-suppressed: {autoHalal} finding(s) audited halal, 0 haram, no admin objection",
            source: "llm", status: "active", ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Auto-review: suppressed keyword term '{Term}' ({Category}) after {Count} wrong flags (rule #{RuleId})",
            term, finding.Category, autoHalal, rule.Id);
        return true;
    }

    internal static string DetectLanguage(string term) =>
        term.Any(c => c >= 0x0600 && c <= 0x06FF) ? "ar" : "en"; // Arabic Unicode block

    private static string BuildPrompt(IReadOnlyList<FilteredContentLog> findings)
    {
        var sb = new StringBuilder(findings.Count * 128);
        sb.AppendLine("Findings to audit:");
        foreach (var f in findings)
        {
            sb.Append('#').Append(f.Id)
              .Append(" [").Append(f.Category).Append(" via ").Append(f.DecisionSource ?? "keyword")
              .Append(", trigger: \"").Append(f.Rule).Append("\", item was ")
              .Append(f.ItemRemoved ? "REMOVED" : "SHOWN").Append("] ")
              .AppendLine(Truncate(f.Content));
        }

        return sb.ToString();
    }

    private static string Truncate(string? text)
    {
        var t = (text ?? string.Empty).Trim().ReplaceLineEndings(" ");
        return t.Length <= 280 ? t : t[..280] + "…";
    }

    private sealed class AuditDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("haram")] public bool Haram { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("wrong_keyword")] public bool WrongKeyword { get; set; }
    }
}

/// <summary>
/// Background shell for <see cref="AutoReviewEngine"/>: audits a batch of unreviewed findings
/// every cycle. Best-effort throughout — a failed cycle logs and waits for the next one; the
/// service is inert when no LLM key is configured.
/// </summary>
public sealed class FindingAutoReviewService : BackgroundService
{
    /// <summary>How often a cycle runs. Long enough to batch, short enough that rules learn same-day.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentFactory _agents;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FindingAutoReviewService> _logger;

    public FindingAutoReviewService(
        IServiceScopeFactory scopeFactory, IAgentFactory agents, IMemoryCache cache,
        ILogger<FindingAutoReviewService> logger)
    {
        _scopeFactory = scopeFactory;
        _agents = agents;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var engine = new AutoReviewEngine(_logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_agents.TryBuildLlm() is { } llm)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var logs = scope.ServiceProvider.GetRequiredService<IFilteredContentLogRepository>();
                    var rules = scope.ServiceProvider.GetRequiredService<IModerationRuleRepository>();

                    var reviewed = await engine.ReviewAsync(llm, logs, rules, _cache, stoppingToken)
                        .ConfigureAwait(false);
                    if (reviewed > 0)
                    {
                        _logger.LogInformation("Auto-review: audited {Count} finding(s)", reviewed);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-review cycle failed; will retry next interval");
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
}
