using Daleel.Core.Observability;
using Daleel.Web.Data;

namespace Daleel.Web.Conversation;

/// <summary>
/// Per-job <see cref="IApiCallObserver"/>: records each external API call (timing + cost) to an
/// internal audit sink, keeps a running cost total, and accumulates the calls for persistence.
/// </summary>
/// <remarks>
/// The per-call detail (provider, endpoint, cost, timing) is deliberately <em>not</em> shown to
/// the user — it goes to the internal audit sink (server logs) and is persisted for analytics.
/// Thread-safe: providers run in parallel during the gather phase. Metering ONLY — cost never
/// cancels an in-flight job (R1); the spend limit blocks NEW searches at submission, and actual
/// spend is charged to credits post-hoc (see <see cref="TotalCredits"/>).
/// </remarks>
public sealed class JobApiCallCollector : IApiCallObserver
{
    private readonly Action<string> _audit;
    private readonly object _gate = new();
    private readonly List<ApiCall> _calls = new();
    private decimal _total;

    /// <param name="audit">Internal audit sink (server-side log) — never surfaced to the user.</param>
    public JobApiCallCollector(Action<string> audit)
    {
        _audit = audit;
    }

    public IReadOnlyList<ApiCall> Calls
    {
        get { lock (_gate) { return _calls.ToList(); } }
    }

    public decimal TotalCost
    {
        get { lock (_gate) { return _total; } }
    }

    /// <summary>Billable credits for the job so far — the per-call prices summed (see <see cref="CreditCost"/>).</summary>
    public int TotalCredits
    {
        get
        {
            lock (_gate)
            {
                // Bill for DELIVERED work only: a failed/timed-out call (incl. a failed-edge attempt
                // that fell back to an inline provider) delivered nothing and must not be charged —
                // otherwise a worker outage double-bills every page it degrades.
                return _calls
                    .Where(c => c.Status == ApiCallStatus.Success)
                    .Sum(c =>
                        CreditCost.ForCall(c.Provider, c.Endpoint, c.InputTokens, c.OutputTokens, c.EstimatedCost));
            }
        }
    }

    public void Record(ApiCall call)
    {
        decimal total;
        lock (_gate)
        {
            _calls.Add(call);
            _total += call.EstimatedCost;
            total = _total;
        }

        _audit(Format(call, total));
    }

    private static string Format(ApiCall c, decimal runningTotal)
    {
        var icon = Icon(c.Provider);
        var summary = string.IsNullOrWhiteSpace(c.RequestSummary) ? "" : $" {Trim(c.RequestSummary!)}";
        var time = c.ResponseTimeMs >= 1000 ? $"{c.ResponseTimeMs / 1000.0:0.0}s" : $"{c.ResponseTimeMs}ms";
        var status = c.Status == ApiCallStatus.Success ? "" : $" [{c.Status}]";
        return $"{icon} {c.Provider}: {c.Endpoint}{summary} ({time}, ~${c.EstimatedCost:0.####}){status} · total ~${runningTotal:0.###}";
    }

    private static string Icon(string provider)
    {
        var p = provider.ToLowerInvariant();
        if (p.Contains("openrouter") || p.Contains("openai") || p.Contains("anthropic")) return "🤖";
        if (p.Contains("places")) return "📍";
        if (p.Contains("context") || p.Contains("cloudflare")) return "📄";
        if (p.Contains("apify")) return "💬";
        return "🔍";
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
