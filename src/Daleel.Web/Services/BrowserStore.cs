using Daleel.Core.Geo;
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

    /// <summary>
    /// Best-guess the visitor's market as a 2-letter country code (from the browser locale/timezone),
    /// for first-visit defaulting. Returns "" / null when nothing usable is found, so the caller can
    /// ask the user instead of silently assuming a market.
    /// </summary>
    public async Task<string?> DetectMarketCodeAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("daleelDetectMarket");
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the browser for the visitor's GPS location (prompting for permission) and maps it to the
    /// supported market that actually CONTAINS it. Returns null when geolocation is denied,
    /// unavailable, times out, or the fix lands outside every supported market — the caller then asks
    /// the user to pick. This is the geolocation step between query-text market detection and prompting.
    /// </summary>
    public async Task<string?> DetectMarketFromLocationAsync()
    {
        try
        {
            var location = await _js.InvokeAsync<BrowserLocation?>("daleelGetLocation");
            // Strict containment, not nearest-by-distance: a consenting user OUTSIDE every supported
            // market (Berlin, Doha…) gets null here and is asked to pick a market in the UI.
            return location is { } loc ? GeoProfiles.MarketContaining(loc.Lat, loc.Lng)?.Key : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Shape of the <c>{lat,lng}</c> object returned by the <c>daleelGetLocation</c> JS helper.</summary>
    private readonly record struct BrowserLocation(double Lat, double Lng);

}
