using Daleel.Core.Observability;

namespace Daleel.Web.Conversation;

/// <summary>
/// Per-job <see cref="IApiCallObserver"/>: streams each external API call to the UI as a
/// progress line with timing + cost, keeps a running cost total, accumulates the calls for
/// persistence, and trips the cost cap (cancelling the job) when the total exceeds the limit.
/// </summary>
/// <remarks>Thread-safe: providers run in parallel during the gather phase.</remarks>
public sealed class JobApiCallCollector : IApiCallObserver
{
    private readonly Action<string> _progress;
    private readonly decimal _maxCost;
    private readonly CancellationTokenSource? _capTrip;
    private readonly object _gate = new();
    private readonly List<ApiCall> _calls = new();
    private decimal _total;
    private bool _capped;

    /// <param name="progress">Live progress sink (streamed to the user's devices).</param>
    /// <param name="maxCost">Max cost per job; 0 = no cap.</param>
    /// <param name="capTrip">Cancelled when the cap is exceeded, to stop the job.</param>
    public JobApiCallCollector(Action<string> progress, decimal maxCost, CancellationTokenSource? capTrip)
    {
        _progress = progress;
        _maxCost = maxCost;
        _capTrip = capTrip;
    }

    public IReadOnlyList<ApiCall> Calls
    {
        get { lock (_gate) { return _calls.ToList(); } }
    }

    public decimal TotalCost
    {
        get { lock (_gate) { return _total; } }
    }

    public void Record(ApiCall call)
    {
        decimal total;
        bool tripNow = false;
        lock (_gate)
        {
            _calls.Add(call);
            _total += call.EstimatedCost;
            total = _total;
            if (!_capped && _maxCost > 0 && _total > _maxCost)
            {
                _capped = true;
                tripNow = true;
            }
        }

        _progress(Format(call, total));

        if (tripNow)
        {
            _progress($"⛔ Cost cap of ${_maxCost:0.###} exceeded (running ${total:0.###}) — stopping search.");
            _capTrip?.Cancel();
        }
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
