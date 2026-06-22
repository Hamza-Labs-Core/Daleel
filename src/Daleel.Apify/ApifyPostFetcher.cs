using Daleel.Apify.ActorInputBuilders;
using Daleel.Apify.Mappers;
using Daleel.Core.Models;
using Daleel.Core.Pipeline;

namespace Daleel.Apify;

/// <summary>
/// <see cref="IPostFetcher"/> backed by Apify actors. Picks the right actor input
/// builder for the source kind, runs the actor, and maps its dataset into
/// <see cref="SocialPost"/>s.
/// </summary>
public class ApifyPostFetcher : IPostFetcher
{
    private readonly ApifyClient _client;
    private readonly string _defaultSearchActor;
    private readonly string _defaultGroupActor;

    public ApifyPostFetcher(
        ApifyClient client,
        string defaultSearchActor = "scrapeforge/facebook-search-posts",
        string defaultGroupActor = "apify/facebook-groups-scraper")
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _defaultSearchActor = defaultSearchActor;
        _defaultGroupActor = defaultGroupActor;
    }

    public async Task<IReadOnlyList<SocialPost>> FetchAsync(
        Source source,
        string? keyword = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var actorId = source.ActorId ?? DefaultActorFor(source.Kind);
        var effectiveKeyword = !string.IsNullOrWhiteSpace(source.Target) && source.Kind == SourceKind.Search
            ? source.Target
            : keyword;

        var input = source.Kind switch
        {
            SourceKind.Search => FacebookSearchBuilder.Build(
                effectiveKeyword ?? throw new InvalidOperationException(
                    $"Search source '{source.Name}' has no keyword to search for."),
                source.MaxItems),

            SourceKind.Group or SourceKind.Page => FacebookGroupBuilder.Build(
                source.Target, source.MaxItems, keyword),

            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unsupported source kind.")
        };

        var items = await _client
            .RunActorAndGetItemsAsync(actorId, input, cancellationToken)
            .ConfigureAwait(false);

        return ApifyPostMapper.MapMany(items, source.Name);
    }

    private string DefaultActorFor(SourceKind kind) => kind switch
    {
        SourceKind.Search => _defaultSearchActor,
        SourceKind.Group or SourceKind.Page => _defaultGroupActor,
        _ => _defaultSearchActor
    };
}
