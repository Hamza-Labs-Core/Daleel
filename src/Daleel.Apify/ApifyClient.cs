using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Daleel.Apify;

/// <summary>
/// Thrown when the Apify API returns an error or an actor run fails.
/// </summary>
public class ApifyException : Exception
{
    public ApifyException(string message) : base(message) { }
    public ApifyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Minimal REST client for the Apify API. It can start an actor run, poll it to
/// completion, and download the resulting dataset items.
/// </summary>
/// <remarks>
/// The Apify "run actor synchronously" pattern is three calls:
/// <list type="number">
///   <item>POST <c>/v2/acts/{actorId}/runs</c> to start a run.</item>
///   <item>GET <c>/v2/actor-runs/{runId}</c> on a loop until status is terminal.</item>
///   <item>GET <c>/v2/datasets/{datasetId}/items</c> to fetch output.</item>
/// </list>
/// Transient HTTP failures (5xx, 429, timeouts) are retried with exponential backoff.
/// </remarks>
public class ApifyClient : IDisposable
{
    private const string DefaultBaseUrl = "https://api.apify.com";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _token;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _runTimeout;
    private readonly int _maxRetries;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <param name="token">Apify API token. Falls back to the <c>APIFY_TOKEN</c> env var.</param>
    /// <param name="httpClient">Optional injected client (for tests / shared handlers).</param>
    /// <param name="pollInterval">How long to wait between run-status polls.</param>
    /// <param name="runTimeout">Maximum time to wait for a run to finish.</param>
    /// <param name="maxRetries">Retry attempts for transient HTTP failures.</param>
    /// <param name="delay">Injectable delay function (tests pass a no-op to avoid real waits).</param>
    public ApifyClient(
        string? token = null,
        HttpClient? httpClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? runTimeout = null,
        int maxRetries = 3,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _token = token
                 ?? Environment.GetEnvironmentVariable("APIFY_TOKEN")
                 ?? throw new ApifyException("No Apify token supplied and APIFY_TOKEN is not set.");

        if (httpClient is null)
        {
            _http = new HttpClient { BaseAddress = new Uri(DefaultBaseUrl) };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _http.BaseAddress ??= new Uri(DefaultBaseUrl);
            _ownsHttp = false;
        }

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _runTimeout = runTimeout ?? TimeSpan.FromMinutes(5);
        _maxRetries = Math.Max(0, maxRetries);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Runs <paramref name="actorId"/> with <paramref name="input"/>, waits for it to
    /// finish, and returns the run's dataset items as a JSON array element.
    /// </summary>
    public async Task<JsonElement> RunActorAndGetItemsAsync(
        string actorId,
        JsonNode input,
        CancellationToken cancellationToken = default)
    {
        var run = await StartRunAsync(actorId, input, cancellationToken).ConfigureAwait(false);
        var finished = await WaitForRunAsync(run.RunId, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(finished.Status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApifyException($"Actor run {finished.RunId} ended with status {finished.Status}.");
        }

        return await GetDatasetItemsAsync(finished.DatasetId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts an actor run and returns its identifiers.</summary>
    public async Task<RunHandle> StartRunAsync(
        string actorId,
        JsonNode input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        // Apify wants the actor id with '/' encoded as '~' in the path.
        var encodedActor = actorId.Replace("/", "~");
        var path = $"/v2/acts/{encodedActor}/runs";
        var body = input.ToJsonString();

        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            },
            cancellationToken).ConfigureAwait(false);

        var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var data = json.RootElement.GetProperty("data");

        return new RunHandle(
            data.GetProperty("id").GetString() ?? throw new ApifyException("Run response missing id."),
            data.TryGetProperty("defaultDatasetId", out var ds) ? ds.GetString() ?? string.Empty : string.Empty,
            data.TryGetProperty("status", out var st) ? st.GetString() ?? "READY" : "READY");
    }

    /// <summary>Polls a run until it reaches a terminal status or times out.</summary>
    public async Task<RunHandle> WaitForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_runTimeout);
        var token = timeoutCts.Token;

        while (true)
        {
            RunHandle status;
            try
            {
                status = await GetRunAsync(runId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ApifyException($"Timed out waiting for run {runId} after {_runTimeout}.");
            }

            if (IsTerminal(status.Status))
            {
                return status;
            }

            try
            {
                await _delay(_pollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ApifyException($"Timed out waiting for run {runId} after {_runTimeout}.");
            }
        }
    }

    /// <summary>Fetches the current state of a run.</summary>
    public async Task<RunHandle> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/v2/actor-runs/{runId}"),
            cancellationToken).ConfigureAwait(false);

        var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var data = json.RootElement.GetProperty("data");

        return new RunHandle(
            data.GetProperty("id").GetString() ?? runId,
            data.TryGetProperty("defaultDatasetId", out var ds) ? ds.GetString() ?? string.Empty : string.Empty,
            data.TryGetProperty("status", out var st) ? st.GetString() ?? "UNKNOWN" : "UNKNOWN");
    }

    /// <summary>Downloads all items from a dataset as a JSON array.</summary>
    public async Task<JsonElement> GetDatasetItemsAsync(string datasetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/v2/datasets/{datasetId}/items?clean=true&format=json"),
            cancellationToken).ConfigureAwait(false);

        var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        // The items endpoint returns a bare JSON array. Clone so the element survives
        // disposal of the parsed document.
        return json.RootElement.Clone();
    }

    private static bool IsTerminal(string status) =>
        status is "SUCCEEDED" or "FAILED" or "ABORTED" or "TIMED-OUT" or "TIMED_OUT";

    /// <summary>
    /// Sends a request, retrying on transient failures with exponential backoff.
    /// The factory is re-invoked per attempt because an <see cref="HttpRequestMessage"/>
    /// cannot be resent.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // 0.5s, 1s, 2s, 4s … capped backoff.
                var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                await _delay(backoff, cancellationToken).ConfigureAwait(false);
            }

            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (!IsTransient(response.StatusCode))
                {
                    var error = await SafeReadBodyAsync(response).ConfigureAwait(false);
                    response.Dispose();
                    throw new ApifyException(
                        $"Apify request failed: {(int)response.StatusCode} {response.StatusCode}. {error}");
                }

                lastError = new ApifyException($"Transient Apify error: {(int)response.StatusCode}.");
                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                response?.Dispose();
                lastError = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Treat per-request timeouts as transient.
                response?.Dispose();
                lastError = ex;
            }
        }

        throw new ApifyException("Apify request failed after retries.", lastError!);
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout ||
        status == (HttpStatusCode)429 ||
        (int)status >= 500;

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Lightweight identifiers + status for an actor run.</summary>
    public readonly record struct RunHandle(string RunId, string DatasetId, string Status);

    // Surface options for callers/tests that want to inspect the parsing config.
    internal static JsonSerializerOptions SerializerOptions => JsonOptions;
}
