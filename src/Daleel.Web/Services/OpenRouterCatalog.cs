using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Daleel.Web.Services;

/// <summary>One model from OpenRouter's public catalogue, normalized for the admin model picker.</summary>
public sealed record OpenRouterModel(
    string Id, string Provider, string Name, decimal? PromptPerMTok, decimal? CompletionPerMTok, int? ContextLength);

/// <summary>Fetches the live OpenRouter model catalogue so the admin model picker never hardcodes a list.</summary>
public interface IOpenRouterCatalog
{
    /// <summary>The available models, briefly cached. <paramref name="forceRefresh"/> bypasses the cache.</summary>
    Task<IReadOnlyList<OpenRouterModel>> GetModelsAsync(bool forceRefresh = false, CancellationToken ct = default);
}

/// <summary>
/// Pulls the model list from OpenRouter's public <c>GET /api/v1/models</c> endpoint (no key required) so
/// the admin per-call-site model picker is always current. Cached briefly to avoid re-fetching on every
/// keystroke; a fetch failure serves the last good list (or empty) rather than throwing — the picker
/// degrades to free text.
/// </summary>
public sealed class OpenRouterCatalog : IOpenRouterCatalog
{
    public const string ModelsUrl = "https://openrouter.ai/api/v1/models";
    private const string CacheKey = "openrouter:models";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OpenRouterCatalog> _logger;

    public OpenRouterCatalog(IHttpClientFactory http, IMemoryCache cache, ILogger<OpenRouterCatalog> logger)
        => (_http, _cache, _logger) = (http, cache, logger);

    public async Task<IReadOnlyList<OpenRouterModel>> GetModelsAsync(
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh &&
            _cache.TryGetValue(CacheKey, out IReadOnlyList<OpenRouterModel>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await client.GetAsync(ModelsUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var models = Parse(json);
            if (models.Count > 0)
            {
                _cache.Set(CacheKey, models, Ttl);
            }

            return models;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch the OpenRouter model catalogue.");
            // Serve the last good list if we have one; otherwise empty (picker falls back to free text).
            return _cache.TryGetValue(CacheKey, out IReadOnlyList<OpenRouterModel>? stale) && stale is not null
                ? stale
                : Array.Empty<OpenRouterModel>();
        }
    }

    /// <summary>Parses an OpenRouter <c>/models</c> response body into the normalized catalogue. Public for
    /// unit testing the (version-sensitive) wire shape without hitting the network.</summary>
    public static IReadOnlyList<OpenRouterModel> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<OpenRouterModel>();
        }

        var list = new List<OpenRouterModel>();
        foreach (var m in data.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = m.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var slash = id!.IndexOf('/');
            var provider = slash > 0 ? id[..slash] : "other";
            var name = m.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() ?? id : id;

            decimal? prompt = null, completion = null;
            if (m.TryGetProperty("pricing", out var pr) && pr.ValueKind == JsonValueKind.Object)
            {
                prompt = PerMTok(pr, "prompt");
                completion = PerMTok(pr, "completion");
            }

            int? context = m.TryGetProperty("context_length", out var cEl) &&
                           cEl.ValueKind == JsonValueKind.Number && cEl.TryGetInt32(out var ci)
                ? ci : null;

            list.Add(new OpenRouterModel(id!, provider, name!, prompt, completion, context));
        }

        return list
            .OrderBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // OpenRouter prices are USD-per-token strings ("0.000003"); present them as USD per million tokens.
    private static decimal? PerMTok(JsonElement pricing, string key) =>
        pricing.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
        decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var perToken)
            ? Math.Round(perToken * 1_000_000m, 3)
            : null;
}
