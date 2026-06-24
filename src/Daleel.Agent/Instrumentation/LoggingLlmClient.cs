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
            _observer.Record(new ApiCall
            {
                Timestamp = DateTimeOffset.UtcNow,
                Provider = "OpenRouter/" + _inner.Provider,
                Endpoint = "chat",
                RequestSummary = response?.Model,
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
}
