using System.Security.Cryptography;
using System.Text;
using Daleel.Core.Arabic;
using Daleel.Core.Models;

namespace Daleel.Pipeline;

/// <summary>
/// Filters out duplicate posts within a single pipeline run by hashing each post's
/// Arabic-normalized text. Cross-posted or lightly-reworded copies that normalize to
/// the same text collapse to one hash and are dropped after the first sighting.
/// </summary>
/// <remarks>
/// The instance is stateful: it remembers every hash it has seen for the lifetime of
/// the run. Create a fresh deduplicator per run (or call <see cref="Reset"/>) to start
/// over. It is not thread-safe; guard it if posts are processed concurrently.
/// </remarks>
public class PostDeduplicator
{
    private readonly HashSet<string> _seen = new();

    /// <summary>Number of distinct posts admitted so far.</summary>
    public int UniqueCount => _seen.Count;

    /// <summary>
    /// Returns true and records the post if it is new; returns false if an equivalent
    /// post has already been seen this run.
    /// </summary>
    public bool IsUnique(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);

        var hash = ComputeHash(post.Text);
        return _seen.Add(hash);
    }

    /// <summary>
    /// Returns only the posts not previously seen, preserving input order, and records
    /// each admitted post so later calls stay consistent.
    /// </summary>
    public IReadOnlyList<SocialPost> Deduplicate(IEnumerable<SocialPost> posts)
    {
        ArgumentNullException.ThrowIfNull(posts);

        var unique = new List<SocialPost>();
        foreach (var post in posts)
        {
            if (IsUnique(post))
            {
                unique.Add(post);
            }
        }

        return unique;
    }

    /// <summary>Clears all remembered hashes.</summary>
    public void Reset() => _seen.Clear();

    /// <summary>
    /// SHA-256 of the normalized text, hex-encoded. Empty/whitespace text hashes to a
    /// stable sentinel so blank posts also dedupe against each other.
    /// </summary>
    public static string ComputeHash(string? text)
    {
        var normalized = ArabicNormalizer.Normalize(text);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
