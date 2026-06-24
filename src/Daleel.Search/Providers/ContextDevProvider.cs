using System.Text.Json;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;

namespace Daleel.Search.Providers;

/// <summary>Brand intelligence returned by Context.dev's <c>/v1/brand</c> endpoint.</summary>
public record BrandProfile
{
    public string Domain { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Industry { get; init; }
    public string? LogoUrl { get; init; }
    public IReadOnlyList<string> Colors { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Socials { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Primary scraping + brand-intelligence provider, backed by
/// <see href="https://context.dev">Context.dev</see>. Replaces the earlier Firecrawl
/// integration: it converts any URL to LLM-ready markdown, extracts structured data
/// against a JSON schema, crawls whole sites, and enriches a domain into a brand profile.
/// </summary>
/// <remarks>
/// Implements <see cref="IScrapeProvider"/> for the common "render a page to markdown"
/// path, and exposes Context.dev-specific extras (<see cref="ExtractAsync"/>,
/// <see cref="GetBrandAsync"/>, <see cref="CrawlAsync"/>) the agent uses directly.
/// Auth is a bearer token from <c>CONTEXT_DEV_API_KEY</c>.
/// </remarks>
public sealed class ContextDevProvider : HttpProviderBase, IScrapeProvider, IExtractProvider
{
    public const string DefaultBaseUrl = "https://api.context.dev";

    private readonly string _apiKey;
    public string Name => "context.dev";
    protected override string ProviderName => Name;

    public ContextDevProvider(
        string? apiKey = null,
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(ConfigureClient(httpClient), maxRetries: 2, delay)
    {
        _apiKey = apiKey
                  ?? Environment.GetEnvironmentVariable("CONTEXT_DEV_API_KEY")
                  ?? throw new ProviderException("CONTEXT_DEV_API_KEY is not set.");
    }

    private static HttpClient ConfigureClient(HttpClient? client)
    {
        client ??= SharedHttpHandler.CreateClient();
        client.BaseAddress ??= new Uri(DefaultBaseUrl);
        return client;
    }

    public async Task<ScrapedPage> ScrapeAsync(
        string url,
        ScrapeFormat format = ScrapeFormat.Markdown,
        CancellationToken cancellationToken = default)
    {
        var path = format == ScrapeFormat.Html ? "/v1/web/scrape/html" : "/v1/web/scrape/markdown";

        try
        {
            using var doc = await PostAsync(path, new { url }, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            var content = FirstString(root, "markdown", "html", "content", "text", "data") ?? string.Empty;
            var title = FirstString(root, "title")
                        ?? (root.TryGetProperty("metadata", out var md) ? FirstString(md, "title") : null);

            return new ScrapedPage
            {
                Url = url,
                Title = title,
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

    /// <summary>
    /// Extracts structured data from a page against a JSON Schema. Returns the extracted
    /// object as a cloned <see cref="JsonElement"/>.
    /// </summary>
    public async Task<JsonElement> ExtractAsync(
        string url,
        object jsonSchema,
        CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync("/v1/web/extract", new { url, schema = jsonSchema }, cancellationToken)
            .ConfigureAwait(false);

        // Result may be under "data"/"extract"/"result" or be the root itself.
        var root = doc.RootElement;
        foreach (var key in new[] { "data", "extract", "result" })
        {
            if (root.TryGetProperty(key, out var inner))
            {
                return inner.Clone();
            }
        }

        return root.Clone();
    }

    /// <summary>Enriches a domain into a brand profile.</summary>
    public async Task<BrandProfile> GetBrandAsync(string domain, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync("/v1/brand", new { domain }, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data))
        {
            root = data;
        }

        var colors = new List<string>();
        if (root.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            colors.AddRange(c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!));
        }

        var socials = new Dictionary<string, string>();
        if (root.TryGetProperty("socials", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in s.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    socials[prop.Name] = prop.Value.GetString()!;
                }
            }
        }

        return new BrandProfile
        {
            Domain = domain,
            Name = FirstString(root, "name", "title"),
            Description = FirstString(root, "description", "summary"),
            Industry = FirstString(root, "industry", "category"),
            LogoUrl = FirstString(root, "logo", "logoUrl", "icon"),
            Colors = colors,
            Socials = socials
        };
    }

    /// <summary>Crawls a site and returns each page's markdown.</summary>
    public async Task<IReadOnlyList<ScrapedPage>> CrawlAsync(
        string url,
        int maxPages = 20,
        CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync("/v1/web/crawl", new { url, limit = maxPages }, cancellationToken)
            .ConfigureAwait(false);

        var pages = new List<ScrapedPage>();
        var root = doc.RootElement;
        var arr = root.TryGetProperty("pages", out var p) ? p
                : root.TryGetProperty("data", out var d) ? d
                : root;

        if (arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                pages.Add(new ScrapedPage
                {
                    Url = FirstString(item, "url") ?? url,
                    Title = FirstString(item, "title"),
                    Content = FirstString(item, "markdown", "content", "text") ?? string.Empty,
                    Provider = Name,
                    Success = true
                });
            }
        }

        return pages;
    }

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken cancellationToken) =>
        await SendJsonAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonBody(body) };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                return req;
            },
            cancellationToken).ConfigureAwait(false);

    private static string? FirstString(JsonElement e, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    return s;
                }
            }
        }

        return null;
    }
}
