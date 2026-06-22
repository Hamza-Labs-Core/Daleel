using System.Globalization;
using System.Text.Json;
using Daleel.Core.Models;

namespace Daleel.Apify.Mappers;

/// <summary>
/// Maps a single Apify dataset item (raw JSON from a Facebook actor) into the
/// platform-agnostic <see cref="SocialPost"/> model.
/// </summary>
/// <remarks>
/// Different actors name the same field differently — the post body might be
/// <c>text</c>, <c>message</c>, <c>message_rich</c>, <c>postText</c>, or <c>content</c>.
/// Rather than bind to one actor's schema we probe a prioritized list of candidate
/// keys for each field, so the mapper survives actor swaps and schema drift.
/// </remarks>
public static class ApifyPostMapper
{
    private static readonly string[] TextKeys =
        { "text", "message", "message_rich", "postText", "content", "caption", "body" };

    private static readonly string[] IdKeys =
        { "id", "postId", "post_id", "facebookId", "legacyId", "url", "postUrl" };

    private static readonly string[] AuthorKeys =
        { "author", "authorName", "user", "userName", "pageName", "ownerName", "from" };

    private static readonly string[] UrlKeys =
        { "url", "postUrl", "permalink", "link", "facebookUrl" };

    private static readonly string[] TimestampKeys =
        { "timestamp", "time", "date", "publishedAt", "createdAt", "creation_time" };

    private static readonly string[] ReactionKeys =
        { "reactions", "reactionsCount", "likes", "likesCount", "totalReactions" };

    /// <summary>
    /// Maps one dataset item. <paramref name="sourceName"/> is stamped onto the result
    /// so downstream consumers know which source produced it.
    /// </summary>
    public static SocialPost Map(JsonElement item, string? sourceName = null)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new SocialPost { Text = item.ToString() ?? string.Empty, Source = sourceName };
        }

        var text = FirstString(item, TextKeys) ?? string.Empty;
        var id = FirstString(item, IdKeys) ?? StableId(text);
        var author = FirstString(item, AuthorKeys, allowNested: true);
        var url = FirstString(item, UrlKeys);
        var timestamp = FirstTimestamp(item, TimestampKeys);
        var reactions = FirstInt(item, ReactionKeys);

        return new SocialPost
        {
            Id = id,
            Text = text,
            Author = author,
            Url = url,
            Timestamp = timestamp,
            Reactions = reactions,
            Source = sourceName
        };
    }

    /// <summary>Maps an entire dataset (array of items).</summary>
    public static IReadOnlyList<SocialPost> MapMany(JsonElement datasetItems, string? sourceName = null)
    {
        if (datasetItems.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SocialPost>();
        }

        var posts = new List<SocialPost>(datasetItems.GetArrayLength());
        foreach (var item in datasetItems.EnumerateArray())
        {
            posts.Add(Map(item, sourceName));
        }

        return posts;
    }

    private static string? FirstString(JsonElement obj, string[] keys, bool allowNested = false)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var s = value.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                    break;

                case JsonValueKind.Number:
                    return value.ToString();

                case JsonValueKind.Object when allowNested:
                    // e.g. author: { name: "…" } — probe common name keys one level down.
                    var nested = FirstString(value, new[] { "name", "title", "fullName", "displayName" });
                    if (nested is not null)
                    {
                        return nested;
                    }
                    break;
            }
        }

        return null;
    }

    private static int? FirstInt(JsonElement obj, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            {
                return n;
            }

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTimeOffset? FirstTimestamp(JsonElement obj, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var raw = value.GetString();
                    if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var dto))
                    {
                        return dto;
                    }
                    break;

                case JsonValueKind.Number:
                    // Heuristic: treat large numbers as Unix seconds (or millis).
                    if (value.TryGetInt64(out var epoch))
                    {
                        return epoch > 100_000_000_000L
                            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                            : DateTimeOffset.FromUnixTimeSeconds(epoch);
                    }
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Derives a deterministic fallback id from the post text when the actor omits one,
    /// so dedup and output stay stable across runs.
    /// </summary>
    private static string StableId(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text));
        return "txt-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
