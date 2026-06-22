using System.Text.Json;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;

namespace Daleel.Search.Providers;

/// <summary>
/// Fallback scraper backed by Cloudflare's
/// <see href="https://developers.cloudflare.com/browser-rendering/">Browser Rendering</see>
/// REST API. Because it renders on Cloudflare's edge with a real headless browser, it
/// gets through most anti-bot defences and JS-heavy pages that simpler fetchers can't.
/// </summary>
/// <remarks>
/// Used when Context.dev can't handle a page, or when custom JS execution is needed.
/// Auth requires both <c>CLOUDFLARE_ACCOUNT_ID</c> (in the URL path) and a bearer
/// <c>CLOUDFLARE_API_TOKEN</c>.
/// </remarks>
public sealed class CloudflareBrowserProvider : HttpProviderBase, IScrapeProvider
{
    public const string DefaultBaseUrl = "https://api.cloudflare.com";

    private readonly string _accountId;
    private readonly string _apiToken;
    public string Name => "cloudflare-browser";
    protected override string ProviderName => Name;

    public CloudflareBrowserProvider(
        string? accountId = null,
        string? apiToken = null,
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(ConfigureClient(httpClient), maxRetries: 2, delay)
    {
        _accountId = accountId
                     ?? Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID")
                     ?? throw new ProviderException("CLOUDFLARE_ACCOUNT_ID is not set.");
        _apiToken = apiToken
                    ?? Environment.GetEnvironmentVariable("CLOUDFLARE_API_TOKEN")
                    ?? throw new ProviderException("CLOUDFLARE_API_TOKEN is not set.");
    }

    private static HttpClient ConfigureClient(HttpClient? client)
    {
        client ??= new HttpClient();
        client.BaseAddress ??= new Uri(DefaultBaseUrl);
        return client;
    }

    private string Endpoint(string action) =>
        $"/client/v4/accounts/{_accountId}/browser-rendering/{action}";

    public async Task<ScrapedPage> ScrapeAsync(
        string url,
        ScrapeFormat format = ScrapeFormat.Markdown,
        CancellationToken cancellationToken = default)
    {
        // Cloudflare exposes dedicated endpoints per output: /markdown, /content (HTML).
        var action = format == ScrapeFormat.Html ? "content" : "markdown";

        try
        {
            var raw = await PostAsync(Endpoint(action), new { url }, cancellationToken).ConfigureAwait(false);
            var content = UnwrapResult(raw);

            return new ScrapedPage
            {
                Url = url,
                Content = content,
                Format = format,
                Provider = Name,
                Success = content.Length > 0
            };
        }
        catch (ProviderException ex)
        {
            return new ScrapedPage { Url = url, Provider = Name, Success = false, Error = ex.Message };
        }
    }

    /// <summary>Renders the page and returns the full HTML.</summary>
    public Task<ScrapedPage> FetchPageAsync(string url, CancellationToken cancellationToken = default) =>
        ScrapeAsync(url, ScrapeFormat.Html, cancellationToken);

    /// <summary>Captures a screenshot, returning the raw image bytes.</summary>
    public async Task<byte[]> ScreenshotAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await Http.SendAsync(
            BuildRequest(Endpoint("screenshot"), new { url }),
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs custom JavaScript on the rendered page (Cloudflare's <c>/scrape</c>/<c>/json</c>
    /// flow) and returns whatever string the script produces.
    /// </summary>
    public async Task<string> ExecuteScriptAsync(string url, string script, CancellationToken cancellationToken = default)
    {
        var raw = await PostAsync(Endpoint("scrape"), new { url, script }, cancellationToken).ConfigureAwait(false);
        return UnwrapResult(raw);
    }

    private async Task<string> PostAsync(string path, object body, CancellationToken cancellationToken) =>
        await SendStringAsync(() => BuildRequest(path, body), cancellationToken).ConfigureAwait(false);

    private HttpRequestMessage BuildRequest(string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonBody(body) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiToken);
        return req;
    }

    /// <summary>
    /// Cloudflare wraps responses as <c>{ "success": true, "result": ... }</c>. Pull the
    /// result out when present; otherwise return the body as-is (e.g. raw HTML/markdown).
    /// </summary>
    private static string UnwrapResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("result", out var result))
                {
                    return result.ValueKind == JsonValueKind.String
                        ? result.GetString() ?? string.Empty
                        : result.GetRawText();
                }
            }
            catch (JsonException)
            {
                // Not JSON after all — fall through to returning the raw body.
            }
        }

        return raw;
    }
}
