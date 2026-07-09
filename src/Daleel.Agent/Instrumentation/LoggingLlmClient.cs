using System.Diagnostics;
using Daleel.Core.Llm;
using Daleel.Core.Observability;

namespace Daleel.Agent.Instrumentation;

/// <summary>
/// Wraps an <see cref="ILlmClient"/> to time each completion, capture token usage, estimate its
/// cost from the model's rate, and report it to an <see cref="IApiCallObserver"/>. Wrapping
/// <see cref="CompleteAsync"/> covers the <c>CompleteTextAsync</c> helper too (it delegates here).
/// </summary>
public sealed class LoggingLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly IApiCallObserver _observer;
    private readonly CostEstimator _estimator;

    public LoggingLlmClient(ILlmClient inner, IApiCallObserver observer, CostEstimator estimator)
        => (_inner, _observer, _estimator) = (inner, observer, estimator);

    public string Provider => _inner.Provider;

    public async Task<LlmResponse> CompleteAsync(
        string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var status = ApiCallStatus.Success;
        LlmResponse? response = null;
        try
        {
            response = await _inner.CompleteAsync(systemPrompt, messages, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) { status = ApiCallStatus.Timeout; throw; }
        catch { status = ApiCallStatus.Error; throw; }
        finally
        {
            sw.Stop();
            // The pipeline step that made this call (ambient, set by the caller). Encoded into Endpoint
            // as "chat:<callSite>" so it groups in the persisted ApiCallLog without a schema change, and
            // also carried structured on ApiCall.CallSite.
            var callSite = LlmCallSiteScope.Current;
            _observer.Record(new ApiCall
            {
                Timestamp = DateTimeOffset.UtcNow,
                Provider = "OpenRouter/" + _inner.Provider,
                Endpoint = callSite is null ? "chat" : "chat:" + callSite,
                CallSite = callSite,
                RequestSummary = response?.Model,
                // What the model actually gave back, capped — an extraction call that returns "{}" or an
                // empty products array is otherwise indistinguishable from a productive one on the
                // efficiency view. Never more than the shared summary cap.
                ResponseSummary = RequestSummaries.Truncate(Preview(response?.Content)),
                Model = response?.Model,
                InputTokens = response?.InputTokens,
                OutputTokens = response?.OutputTokens,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                ResponseBytes = response?.Content?.Length ?? 0,
                Status = status,
                EstimatedCost = _estimator.EstimateLlm(response?.Model, response?.InputTokens, response?.OutputTokens)
            });
        }
    }

    /// <summary>A single-line, whitespace-collapsed head of the model's reply — enough to see whether the
    /// call produced anything, never the full completion.</summary>
    private static string? Preview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "(empty)";
        }

        var flat = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 240 ? flat[..240] : flat;
    }
}
