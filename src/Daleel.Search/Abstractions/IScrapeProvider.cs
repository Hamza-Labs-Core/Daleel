namespace Daleel.Search.Abstractions;

/// <summary>The output format requested from a scrape.</summary>
public enum ScrapeFormat
{
    /// <summary>Clean, LLM-ready markdown (default — what we feed to the model).</summary>
    Markdown,

    /// <summary>Raw rendered HTML.</summary>
    Html,

    /// <summary>Main text content, stripped of nav/ads.</summary>
    Text
}

/// <summary>The result of rendering and extracting a page.</summary>
public record ScrapedPage
{
    public string Url { get; init; } = string.Empty;
    public string? Title { get; init; }

    /// <summary>The extracted content in the requested format.</summary>
    public string Content { get; init; } = string.Empty;

    public ScrapeFormat Format { get; init; } = ScrapeFormat.Markdown;
    public string Provider { get; init; } = string.Empty;

    /// <summary>True when the scrape succeeded and <see cref="Content"/> is usable.</summary>
    public bool Success { get; init; } = true;

    public string? Error { get; init; }
}

/// <summary>
/// Renders a (possibly JS-heavy) page and extracts its content. Implemented by
/// Context.dev (primary) and Cloudflare Browser Rendering (fallback).
/// </summary>
public interface IScrapeProvider
{
    string Name { get; }

    Task<ScrapedPage> ScrapeAsync(
        string url,
        ScrapeFormat format = ScrapeFormat.Markdown,
        CancellationToken cancellationToken = default);
}
