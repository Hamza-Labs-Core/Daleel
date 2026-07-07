using System.Text.Json;
using Daleel.Search.Abstractions;

namespace Daleel.Search;

/// <summary>
/// Routes a scrape through an ordered list of providers, falling back to the next when
/// one fails or returns empty content. Configured as Context.dev → Cloudflare Browser so
/// the cheap/clean path is tried first and the heavy headless-browser path is the
/// fallback for pages that need it.
/// </summary>
/// <remarks>
/// The router itself is BOTH an <see cref="IScrapeProvider"/> (markdown) and an
/// <see cref="IExtractProvider"/> (schema extraction), so callers that feature-detect
/// <c>_scraper is IExtractProvider</c> still see the capability through the router — without
/// this, adding a second provider to the chain silently HID the extraction capability and
/// per-URL store extraction returned nothing. Scrape and extract use OPPOSITE chain orders on
/// purpose: markdown scraping tries the cheap provider first (chain order), while structured
/// extraction of JS-heavy store/marketplace pages tries the most capable renderer — the headless
/// browser — first (reverse order), falling back to a lighter extractor when it yields nothing.
/// </remarks>
public sealed class ScrapeRouter : IScrapeProvider, IExtractProvider
{
    private readonly IReadOnlyList<IScrapeProvider> _chain;
    private readonly IReadOnlyList<IExtractProvider> _extractChain;

    public string Name => "scrape-router";

    public ScrapeRouter(params IScrapeProvider[] chain)
    {
        if (chain is null || chain.Length == 0)
        {
            throw new ArgumentException("At least one scrape provider is required.", nameof(chain));
        }

        _chain = chain;
        // Cheap-first for scraping → browser-first for extraction: reverse the extract-capable members.
        _extractChain = chain.OfType<IExtractProvider>().Reverse().ToList();
    }

    public async Task<ScrapedPage> ScrapeAsync(
        string url,
        ScrapeFormat format = ScrapeFormat.Markdown,
        CancellationToken cancellationToken = default)
    {
        ScrapedPage? lastFailure = null;

        foreach (var provider in _chain)
        {
            ScrapedPage page;
            try
            {
                page = await provider.ScrapeAsync(url, format, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = new ScrapedPage { Url = url, Provider = provider.Name, Success = false, Error = ex.Message };
                continue;
            }

            if (page.Success && !string.IsNullOrWhiteSpace(page.Content))
            {
                return page;
            }

            lastFailure = page;
        }

        return lastFailure ?? new ScrapedPage
        {
            Url = url,
            Provider = Name,
            Success = false,
            Error = "All scrape providers failed."
        };
    }

    /// <summary>
    /// Structured extraction across the extract-capable members, browser-first (see class remarks).
    /// Returns the first result that actually carries products, falling back to the next extractor on
    /// an empty result or a failure; yields an empty object when no member produces anything.
    /// </summary>
    public async Task<JsonElement> ExtractAsync(
        string url, object jsonSchema, CancellationToken cancellationToken = default)
    {
        JsonElement? last = null;

        foreach (var extractor in _extractChain)
        {
            JsonElement result;
            try
            {
                result = await extractor.ExtractAsync(url, jsonSchema, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            if (HasProducts(result))
            {
                return result;
            }

            last = result;
        }

        return last ?? EmptyObject();
    }

    /// <summary>
    /// The wrapper keys under which an extractor may return the product array — kept in lockstep with
    /// <c>ListingExtractor.ResolveProductsArray</c> so a valid browser result under an alternate key
    /// isn't judged empty here and needlessly re-extracted (and re-billed) by the fallback provider.
    /// </summary>
    private static readonly string[] ProductArrayKeys = { "products", "items", "listings", "results" };

    /// <summary>
    /// True when an extraction result carries at least one product — tolerant of a bare array and of
    /// every wrapper key <c>ListingExtractor.FromExtractedJson</c> accepts. Drives the
    /// browser→lighter-extractor fallback.
    /// </summary>
    private static bool HasProducts(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.GetArrayLength() > 0;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var key in ProductArrayKeys)
        {
            if (root.TryGetProperty(key, out var arr) &&
                arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
