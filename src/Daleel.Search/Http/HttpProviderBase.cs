using System.Net;
using System.Text;
using System.Text.Json;

namespace Daleel.Search.Http;

/// <summary>Thrown when an external search/scrape provider returns an error.</summary>
public class ProviderException : Exception
{
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

    protected static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected HttpProviderBase(
        HttpClient http,
        int maxRetries = 2,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Http = http ?? throw new ArgumentNullException(nameof(http));
        _maxRetries = Math.Max(0, maxRetries);
        _delay = delay ?? Task.Delay;
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
            try
            {
                using var request = requestFactory();
                response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (!IsTransient(response.StatusCode))
                {
                    var body = await SafeBodyAsync(response).ConfigureAwait(false);
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new ProviderException($"{ProviderName}: HTTP {(int)status} {status}. {body}");
                }

                last = new ProviderException($"{ProviderName}: transient HTTP {(int)response.StatusCode}.");
                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                response?.Dispose();
                last = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                last = ex;
            }
        }

        throw new ProviderException($"{ProviderName}: request failed after retries.", last!);
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
