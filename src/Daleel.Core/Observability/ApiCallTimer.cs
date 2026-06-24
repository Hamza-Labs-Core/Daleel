using System.Diagnostics;

namespace Daleel.Core.Observability;

/// <summary>
/// Times an external call, classifies its outcome, estimates its cost, and emits an
/// <see cref="ApiCall"/> to the observer — always, even when the call throws. Centralizes the
/// try/finally + stopwatch boilerplate so every decorator stays a one-liner.
/// </summary>
public static class ApiCallTimer
{
    /// <summary>Times a non-LLM call. <paramref name="bytes"/> optionally measures the response size.</summary>
    public static async Task<T> TimeAsync<T>(
        IApiCallObserver? observer,
        CostEstimator estimator,
        string provider,
        string endpoint,
        string? summary,
        Func<Task<T>> action,
        Func<T, long>? bytes = null)
    {
        if (observer is null)
        {
            return await action().ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();
        var status = ApiCallStatus.Success;
        var result = default(T);
        try
        {
            result = await action().ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            status = ApiCallStatus.Timeout;
            throw;
        }
        catch
        {
            status = ApiCallStatus.Error;
            throw;
        }
        finally
        {
            sw.Stop();
            var size = status == ApiCallStatus.Success && bytes is not null && result is not null ? bytes(result) : 0;
            observer.Record(new ApiCall
            {
                Timestamp = DateTimeOffset.UtcNow,
                Provider = provider,
                Endpoint = endpoint,
                RequestSummary = summary,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                ResponseBytes = size,
                Status = status,
                EstimatedCost = estimator.EstimateCall(provider, endpoint)
            });
        }
    }
}
