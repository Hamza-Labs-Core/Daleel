using System.Text.Json;
using Daleel.Search.Abstractions;
using Daleel.Search.Http;

namespace Daleel.Search.Providers;

/// <summary>One product from Context.dev's brand-catalogue extraction (<c>/v1/brand/ai/products</c>).</summary>
public record CatalogProduct
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public decimal? Price { get; init; }
    public string? Currency { get; init; }
    public string? Url { get; init; }
    public string? Category { get; init; }
    public string? ImageUrl { get; init; }
    public string? Sku { get; init; }
}

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

    /// <summary>
    /// The code Context.dev returns once the account's usage quota is spent. Hitting it is EXPECTED,
    /// not a bug: the provider chain exists precisely so Cloudflare Browser takes over from here.
    /// </summary>
    private const string DepletionMarker = "USAGE_EXCEEDED";

    /// <summary>
    /// Latched true the first time Context.dev reports <see cref="DepletionMarker"/>. Depletion is a
    /// run-long steady state, so once seen, every further call is a guaranteed miss plus three wasted
    /// retry round-trips per page. The latch short-circuits those calls straight to the fallback.
    /// Monotonic (only ever false→true), so a plain volatile read is enough — a rare torn read just
    /// costs one extra HTTP attempt, never wrong behaviour.
    /// </summary>
    private volatile bool _depleted;

    /// <summary>Whether Context.dev has reported its quota exhausted this run (see <see cref="_depleted"/>).</summary>
    public bool IsDepleted => _depleted;

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
        // Context.dev's web endpoints are GET with the url as a query param — NOT POST with a JSON
        // body. Posting returned "API you have tried to access does not exist", which silently routed
        // every scrape to the Cloudflare fallback. Verified against the live API: GET
        // /v1/web/scrape/markdown?url=… → 200 { "success": true, "markdown": "…" }.
        var kind = format == ScrapeFormat.Html ? "html" : "markdown";
        var path = $"/v1/web/scrape/{kind}?url={Uri.EscapeDataString(url)}";

        try
        {
            using var doc = await GetAsync(path, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Enriches a domain into a brand profile (GET /v1/brand/retrieve?domain=…).</summary>
    public async Task<BrandProfile> GetBrandAsync(string domain, CancellationToken cancellationToken = default)
    {
        using var doc = await GetAsync($"/v1/brand/retrieve?domain={Uri.EscapeDataString(domain)}", cancellationToken)
            .ConfigureAwait(false);
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

    /// <summary>
    /// Extracts a brand/store's product catalogue WITH PRICING from its website
    /// (<c>POST /v1/brand/ai/products</c>) — the purpose-built endpoint for "scrape the models + prices
    /// a site sells". Slow (it crawls + AI-extracts), so callers should run it off the hot path and give
    /// it a generous timeout. Best-effort: returns empty on any failure.
    /// </summary>
    public async Task<IReadOnlyList<CatalogProduct>> ExtractProductsAsync(
        string domain, int maxProducts = 0, int timeoutMs = 45_000, CancellationToken cancellationToken = default)
    {
        try
        {
            // maxProducts ≤ 0 ⇒ UNCAPPED: omit the field so Context.dev applies its own ceiling.
            // Only an explicit caller-chosen cap is forwarded.
            object payload = maxProducts > 0
                ? new { domain, maxProducts, timeoutMS = timeoutMs }
                : new { domain, timeoutMS = timeoutMs };
            using var doc = await PostAsync(
                "/v1/brand/ai/products",
                payload,
                cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("products", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CatalogProduct>();
            }

            var list = new List<CatalogProduct>();
            foreach (var p in arr.EnumerateArray())
            {
                var name = FirstString(p, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                list.Add(new CatalogProduct
                {
                    Name = name,
                    Description = FirstString(p, "description"),
                    Price = p.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Number
                            && pr.TryGetDecimal(out var d) ? d : null,
                    Currency = FirstString(p, "currency"),
                    Url = FirstString(p, "url"),
                    Category = FirstString(p, "category"),
                    ImageUrl = FirstString(p, "image_url", "imageUrl"),
                    Sku = FirstString(p, "sku")
                });
            }

            return list;
        }
        catch (ProviderException)
        {
            return Array.Empty<CatalogProduct>();
        }
    }

    /// <summary>GET helper — Context.dev's web/brand endpoints take query params with a Bearer token.</summary>
    private Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken cancellationToken) =>
        SendTrackedAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            return req;
        }, cancellationToken);

    private Task<JsonDocument> PostAsync(string path, object body, CancellationToken cancellationToken) =>
        SendTrackedAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonBody(body) };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            return req;
        }, cancellationToken);

    /// <summary>
    /// Wraps every Context.dev call with the depletion latch: once the quota is spent, fail fast
    /// (before any HTTP) so the router drops to Cloudflare Browser instantly instead of re-hammering a
    /// dead quota. Otherwise it watches both the response body and the (post-retry) error message for
    /// <see cref="DepletionMarker"/> and latches it for every subsequent call this run.
    /// </summary>
    private async Task<JsonDocument> SendTrackedAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        if (_depleted)
        {
            throw new ProviderException(
                $"{ProviderName}: usage quota exhausted ({DepletionMarker}); short-circuiting to the fallback provider.")
            {
                StatusCode = 429,
            };
        }

        try
        {
            var doc = await SendJsonAsync(requestFactory, cancellationToken).ConfigureAwait(false);
            NoteDepletion(doc.RootElement); // A depletion payload can arrive as a 200 with success:false.
            return doc;
        }
        catch (ProviderException ex)
        {
            NoteDepletion(ex.Message); // A quota HTTP error folds its body (the marker) into the message.
            throw;
        }
    }

    /// <summary>Latches <see cref="_depleted"/> when the marker appears anywhere in a response/error string.</summary>
    private void NoteDepletion(string? text)
    {
        if (!_depleted && text is not null &&
            text.Contains(DepletionMarker, StringComparison.OrdinalIgnoreCase))
        {
            _depleted = true;
        }
    }

    /// <summary>
    /// Safety net for a depletion signalled as an HTTP 200 whose body carries the marker in a small
    /// status field (<c>error</c>/<c>code</c>/<c>message</c>/<c>reason</c>) — checked without
    /// re-serializing large scrape payloads.
    /// </summary>
    private void NoteDepletion(JsonElement root)
    {
        if (_depleted || root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var key in new[] { "error", "code", "message", "reason" })
        {
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                NoteDepletion(v.GetString());
            }
        }
    }

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
