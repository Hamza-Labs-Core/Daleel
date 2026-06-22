using Microsoft.JSInterop;

namespace Daleel.Web.Services;

/// <summary>
/// A thin wrapper over the browser's <c>localStorage</c>. Settings (default geo/model, theme,
/// and BYO API keys) live in the browser only — they are read back into the live Server circuit
/// on demand and passed straight to the agent, so they are never persisted in server logs.
/// </summary>
/// <remarks>
/// All access happens via JS interop, which is unavailable during prerender/static SSR. Callers
/// must therefore only invoke these from <c>OnAfterRenderAsync(firstRender)</c> or later; the
/// methods swallow the prerender <see cref="InvalidOperationException"/> defensively regardless.
/// </remarks>
public sealed class BrowserStore
{
    private const string Prefix = "daleel.";
    private readonly IJSRuntime _js;

    public BrowserStore(IJSRuntime js) => _js = js;

    /// <summary>Reads a string value, or null if absent / unavailable.</summary>
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            return await _js.InvokeAsync<string?>("localStorage.getItem", Prefix + key);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Writes a string value (no-op if storage is unavailable).</summary>
    public async Task SetAsync(string key, string value)
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", Prefix + key, value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or TaskCanceledException)
        {
            // ignored — prerender or storage disabled
        }
    }

    /// <summary>Removes a value (no-op if storage is unavailable).</summary>
    public async Task RemoveAsync(string key)
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", Prefix + key);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or TaskCanceledException)
        {
            // ignored
        }
    }

    /// <summary>The API-key names the Settings page manages and the agent consumes.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Help)> ApiKeyNames = new[]
    {
        ("OPENROUTER_API_KEY", "OpenRouter", "Recommended — one key, every model"),
        ("OPENAI_API_KEY", "OpenAI", "Used if no OpenRouter key"),
        ("ANTHROPIC_API_KEY", "Anthropic", "Used if no OpenRouter/OpenAI key"),
        ("SERPAPI_KEY", "SerpApi", "Web & shopping search"),
        ("BING_SEARCH_KEY", "Bing Search", "Fallback web search"),
        ("GOOGLE_PLACES_API_KEY", "Google Places", "Store finder"),
        ("CONTEXT_DEV_API_KEY", "Context.dev", "Page scraping"),
        ("APIFY_TOKEN", "Apify", "Social monitoring"),
    };

    /// <summary>Reads all stored API keys into a dictionary for handing to the agent factory.</summary>
    public async Task<Dictionary<string, string>> GetKeysAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _, _) in ApiKeyNames)
        {
            var value = await GetAsync("key." + name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[name] = value;
            }
        }

        return result;
    }
}
