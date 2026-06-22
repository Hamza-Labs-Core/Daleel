using Daleel.Core.Models;

namespace Daleel.Core.Pipeline;

/// <summary>
/// A matched post paired with the match metadata that flagged it. This is the unit
/// the pipeline emits to a <see cref="IResultWriter"/>.
/// </summary>
public record MatchedPost
{
    public required SocialPost Post { get; init; }
    public required MatchResult Match { get; init; }
}

/// <summary>
/// Persists matched posts somewhere durable (a JSONL file, a database, stdout…).
/// </summary>
public interface IResultWriter : IAsyncDisposable
{
    /// <summary>Writes a single matched post.</summary>
    Task WriteAsync(MatchedPost result, CancellationToken cancellationToken = default);
}
