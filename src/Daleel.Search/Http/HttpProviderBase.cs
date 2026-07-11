using System.Net;
using System.Text;
using System.Text.Json;

namespace Daleel.Search.Http;

/// <summary>Thrown when an external search/scrape provider returns an error.</summary>
public class ProviderException : Exception
{
    /// <summary>The HTTP status the provider returned, when the failure was an HTTP response (else null).</summary>
    public int? StatusCode { get; init; }

    /// <summary>True when this is a transient/infra failure (retries exhausted, timeout) rather than a hard error.</summary>
    public bool IsTransient { get; init; }

    public ProviderException(string message) : base(message) { }
    public ProviderException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Shared plumbing for HTTP-backed providers: an injectable <see cref="HttpClient"/>
/// (so tests can supply a stub handler), JSON helpers, and transient-error retry with
/// exponential backoff. Delay is injectable so tests run without real waits.
/// </summary>
public abstract class HttpProviderBase
{
    protected HttpClient Http { get; }
    private readonly int _maxRetries;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan? _perAttemptTimeout;

    protected static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <param name="perAttemptTimeout">
    /// Hard wall-clock cap on each individual send attempt (connect + send + read the buffered
    /// response). Enforced with a per-attempt linked <see cref="CancellationTokenSource"/> so it is
    /// independent of the fragile <see cref="HttpClient.Timeout"/> — a stalled connect callback,
    /// pooled-connection reuse, or a slow-trickle keep-alive response can't defeat it. A timed-out
    /// attempt is treated as transient and retried; null leaves attempts unbounded (the historical
    /// behaviour, used by the slower scrape providers).
    /// </param>
    protected HttpProviderBase(
        HttpClient http,
        int maxRetries = 2,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? perAttemptTimeout = null)
    {
        Http = http ?? throw new ArgumentNullException(nameof(http));
        _maxRetries = Math.Max(0, maxRetries);
        _delay = delay ?? Task.Delay;
        _perAttemptTimeout = perAttemptTimeout;
    }

    /// <summary>Sends a request with retry, returning the parsed JSON document.</summary>
    protected async Task<JsonDocument> SendJsonAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(requestFactory, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request with retry, returning the raw response body as a string.</summary>
    protected async Task<string> SendStringAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(requestFactory, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    protected static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        Exception? last = null;

        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                await _delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt - 1)), cancellationToken)
                    .ConfigureAwait(false);
            }

            HttpResponseMessage? response = null;
            // Bound each attempt: a linked CTS that auto-cancels after the per-attempt timeout (if any),
            // while still honouring a genuine outer cancellation (cost cap, user cancel, workflow deadline).
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_perAttemptTimeout is { } perAttempt)
            {
                attemptCts.CancelAfter(perAttempt);
            }

            try
            {
                using var request = requestFactory();
                response = await Http.SendAsync(request, attemptCts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (!IsTransient(response.StatusCode))
                {
                    var body = await SafeBodyAsync(response).ConfigureAwait(false);
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new ProviderException($"{ProviderName}: HTTP {(int)status} {status}. {body}")
                    {
                        StatusCode = (int)status,
                    };
                }

                last = new ProviderException($"{ProviderName}: transient HTTP {(int)response.StatusCode}.");
                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                response?.Dispose();
                last = ex;
            }
            // A per-attempt timeout surfaces as a cancellation on attemptCts while the OUTER token is
            // still live — treat it as a transient timeout and retry. A real outer cancellation falls
            // through (this filter is false) and propagates.
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                last = _perAttemptTimeout is { } t
                    ? new ProviderException($"{ProviderName}: attempt timed out after {t.TotalSeconds:0}s.", ex)
                    : ex;
            }
        }

        throw new ProviderException($"{ProviderName}: request failed after retries.", last!)
        {
            IsTransient = true,
            StatusCode = (last as ProviderException)?.StatusCode,
        };
    }

    /// <summary>
    /// Resolves a per-attempt wall-clock timeout from an optional whole-seconds env override,
    /// falling back to <paramref name="defaultSeconds"/> and clamping the result to 1–30s. Providers
    /// pass the result as <c>perAttemptTimeout</c> so a stalled vendor call is bounded to seconds
    /// instead of riding <see cref="HttpClient"/>'s 100s default across every retry — which is what
    /// let a slow primary (SerpAPI) block discovery for minutes before the router could fail over.
    /// </summary>
    protected static TimeSpan ResolveAttemptTimeout(string envVar, int defaultSeconds)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        var seconds = int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : defaultSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30));
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout ||
        status == (HttpStatusCode)429 ||
        (int)status >= 500;

    private static async Task<string> SafeBodyAsync(HttpResponseMessage response)
    {
        try { return await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    /// <summary>Provider name, surfaced in error messages.</summary>
    protected abstract string ProviderName { get; }
}
