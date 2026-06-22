using Daleel.Search.Abstractions;

namespace Daleel.Search;

/// <summary>
/// Routes a scrape through an ordered list of providers, falling back to the next when
/// one fails or returns empty content. Configured as Context.dev → Cloudflare Browser so
/// the cheap/clean path is tried first and the heavy headless-browser path is the
/// fallback for pages that need it.
/// </summary>
public sealed class ScrapeRouter : IScrapeProvider
{
    private readonly IReadOnlyList<IScrapeProvider> _chain;

    public string Name => "scrape-router";

    public ScrapeRouter(params IScrapeProvider[] chain)
    {
        if (chain is null || chain.Length == 0)
        {
            throw new ArgumentException("At least one scrape provider is required.", nameof(chain));
        }

        _chain = chain;
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
}
