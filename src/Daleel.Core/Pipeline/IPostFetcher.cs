using Daleel.Core.Models;

namespace Daleel.Core.Pipeline;

/// <summary>
/// Fetches raw posts from a <see cref="Source"/>. Implementations may call external
/// services (Apify), read local fixtures, or return canned data for tests.
/// </summary>
public interface IPostFetcher
{
    /// <summary>
    /// Fetches up to <see cref="Source.MaxItems"/> posts for <paramref name="source"/>.
    /// The optional <paramref name="keyword"/> seeds search-style sources.
    /// </summary>
    Task<IReadOnlyList<SocialPost>> FetchAsync(
        Source source,
        string? keyword = null,
        CancellationToken cancellationToken = default);
}
