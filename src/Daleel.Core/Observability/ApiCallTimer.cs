using System.Diagnostics;

namespace Daleel.Core.Observability;

/// <summary>
/// Times an external call, classifies its outcome, estimates its cost, and emits an
/// <see cref="ApiCall"/> to the observer — always, even when the call throws. Centralizes the
/// try/finally + stopwatch boilerplate so every decorator stays a one-liner.
/// </summary>
public static class ApiCallTimer
{
    /// <summary>
    /// Times a non-LLM call. <paramref name="bytes"/> optionally measures the response size.
    /// <paramref name="success"/> optionally judges whether a NON-throwing result actually delivered
    /// (e.g. an edge worker that returns a page with <c>Success == false</c> rather than throwing):
    /// a call that returned but did not deliver is recorded as <see cref="ApiCallStatus.Error"/> at
    /// ZERO cost, so a failed-edge-then-inline-fallback bills once, not twice.
    /// </summary>
    public static async Task<T> TimeAsync<T>(
        IApiCallObserver? observer,
        CostEstimator estimator,
        string provider,
        string endpoint,
        string? summary,
        Func<Task<T>> action,
        Func<T, long>? bytes = null,
        Func<T, bool>? success = null)
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
            // A non-throwing call that the predicate judges undelivered is a billable NON-event:
            // downgrade to Error and charge nothing, so the fallback that follows carries the bill.
            var delivered = status == ApiCallStatus.Success
                && (success is null || (result is not null && success(result)));
            var recordedStatus = status == ApiCallStatus.Success && !delivered ? ApiCallStatus.Error : status;
            var size = delivered && bytes is not null && result is not null ? bytes(result) : 0;
            observer.Record(new ApiCall
            {
                Timestamp = DateTimeOffset.UtcNow,
                Provider = provider,
                Endpoint = endpoint,
                RequestSummary = summary,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                ResponseBytes = size,
                Status = recordedStatus,
                EstimatedCost = delivered ? estimator.EstimateCall(provider, endpoint) : 0m
            });
        }
    }
}
