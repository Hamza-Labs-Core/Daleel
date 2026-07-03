using Daleel.Core.Models;

namespace Daleel.Core.Moderation;

/// <summary>
/// Describes how one item type is moderated: its named text fields (so a finding can say WHERE
/// the match was), its source/image URLs (for the admin log's links and thumbnails), and — when
/// images can be stripped — how to rebuild the item without its image.
/// </summary>
public sealed record ModerationProjection<T>(
    string Kind,
    Func<T, IReadOnlyList<(string Field, string? Text)>> Fields,
    Func<T, string?>? SourceUrl = null,
    Func<T, string?>? ImageUrl = null,
    Func<T, string?, T>? WithImageUrl = null)
{
    /// <summary>All fields joined into one classifier-ready text.</summary>
    public string JoinedText(T item) =>
        string.Join(" · ", Fields(item)
            .Select(f => f.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));
}

/// <summary>Built-in projections for the Core models the gather chokepoint moderates.</summary>
public static class ModerationProjections
{
    public static readonly ModerationProjection<StoreLocation> Store = new(
        "StoreLocation",
        s => new[] { ("name", (string?)s.Name), ("address", s.Address) },
        SourceUrl: s => s.Website ?? s.GoogleMapsUrl);

    public static readonly ModerationProjection<SocialPost> Social = new(
        "SocialPost",
        p => new[] { ("text", (string?)p.Text), ("author", p.Author) },
        SourceUrl: p => p.Url);
}

/// <summary>
/// The item-level moderation pipeline. Composes three layers over one shared audit
/// (<see cref="ContentFilter"/>):
/// <list type="number">
/// <item><b>Whitelist</b> — items an admin explicitly un-filtered bypass everything.</item>
/// <item><b>Keyword</b> — the deterministic bilingual blocklist flags candidates.</item>
/// <item><b>LLM adjudication</b> (when configured) — one batched call re-judges every keyword flag
/// in context (overturning false positives) and adds context-aware flags of its own, subject to
/// the per-category confidence thresholds learned from admin ratings.</item>
/// </list>
/// Kept items with an image can then be screened by a vision classifier, which strips a haram
/// IMAGE (recording a finding) without removing the item — granularity is per-image, per-item,
/// never per-site.
/// </summary>
/// <remarks>
/// Failure contract: classifier errors degrade to keyword-only behavior; moderation never faults
/// a search. When no LLM is configured the moderator behaves exactly like the legacy keyword
/// filter (plus whitelist), so the pipeline works with zero keys.
/// </remarks>
public sealed class HalalModerator
{
    private readonly ContentFilter _filter;
    private readonly IHalalClassifier? _classifier;
    private readonly IHalalImageClassifier? _imageClassifier;
    private readonly HalalPolicy _policy;
    private readonly Action<string>? _log;

    // Cross-call budget: one gather moderates several lists (web, shopping, stores, social) but
    // the image cap is per RUN, so the remaining budget is shared state on this instance.
    private int _imageBudget;

    public HalalModerator(
        ContentFilter filter,
        IHalalClassifier? classifier = null,
        IHalalImageClassifier? imageClassifier = null,
        HalalPolicy? policy = null,
        Action<string>? log = null)
    {
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _classifier = classifier;
        _imageClassifier = imageClassifier;
        _policy = policy ?? new HalalPolicy();
        _log = log;
        _imageBudget = _policy.MaxImagesPerRun;
    }

    /// <summary>The shared filter carrying the audit log (findings) for this run.</summary>
    public ContentFilter Filter => _filter;

    /// <summary>Moderates one list of items at item granularity. See class remarks for the layers.</summary>
    public async Task<List<T>> ModerateAsync<T>(
        IReadOnlyList<T> items, ModerationProjection<T> projection, CancellationToken ct = default)
    {
        if (items.Count == 0 || _filter.Strictness == FilterStrictness.Off)
        {
            return items.ToList();
        }

        // ── Stage 1+2: whitelist check and keyword candidates ────────────────────
        var states = new List<ItemState<T>>(items.Count);
        foreach (var item in items)
        {
            var fields = projection.Fields(item);
            var text = projection.JoinedText(item);
            var state = new ItemState<T>(
                item, fields, text,
                ModerationKeys.HashContent(text),
                projection.SourceUrl?.Invoke(item),
                projection.ImageUrl?.Invoke(item));

            if (_filter.IsWhitelisted(state.SourceUrl, state.ImageUrl, state.ContentHash))
            {
                state.Whitelisted = true;
            }
            else if (_filter.MatchFields(fields) is { } m)
            {
                state.KeywordMatch = m;
            }

            states.Add(state);
        }

        // ── Stage 3: LLM adjudication (best-effort) ──────────────────────────────
        if (_classifier is { IsConfigured: true })
        {
            await AdjudicateAsync(states, projection.Kind, ct).ConfigureAwait(false);
        }

        // Keyword flags the LLM never saw (no LLM configured, call failed, or its response
        // skipped the item) become removals — the deterministic baseline is authoritative.
        FinalizeKeywordFlags(states, projection.Kind);

        // ── Decide, record findings, collect kept items ─────────────────────────
        var kept = new List<T>(states.Count);
        foreach (var state in states)
        {
            if (state.Removal is { } removal)
            {
                _filter.RecordFinding(removal with
                {
                    Kind = projection.Kind,
                    Content = state.Text,
                    SourceUrl = state.SourceUrl,
                    ImageUrl = state.ImageUrl,
                    ContentHash = state.ContentHash,
                    ItemRemoved = true
                });
            }
            else
            {
                kept.Add(state.Item);
            }
        }

        // ── Stage 4: per-image vision screening of kept items ────────────────────
        if (_imageClassifier is { IsConfigured: true } && projection.WithImageUrl is not null)
        {
            kept = await ModerateImagesAsync(kept, projection, states, ct).ConfigureAwait(false);
        }

        return kept;
    }

    private async Task AdjudicateAsync<T>(List<ItemState<T>> states, string kind, CancellationToken ct)
    {
        var candidates = new List<HalalCandidate>();
        for (var i = 0; i < states.Count; i++)
        {
            var s = states[i];
            if (s.Whitelisted || string.IsNullOrWhiteSpace(s.Text))
            {
                continue;
            }

            candidates.Add(new HalalCandidate(
                i, s.Text, kind, s.KeywordMatch?.Category, s.KeywordMatch?.Term));
        }

        if (candidates.Count == 0)
        {
            return;
        }

        IReadOnlyList<HalalVerdict> verdicts;
        try
        {
            verdicts = await _classifier!.ClassifyAsync(candidates, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            verdicts = Array.Empty<HalalVerdict>();
        }

        var byId = verdicts.ToDictionary(v => v.Id);
        foreach (var candidate in candidates)
        {
            var state = states[candidate.Id];
            var verdict = byId.GetValueOrDefault(candidate.Id);

            if (state.KeywordMatch is { } kw)
            {
                if (verdict is null)
                {
                    continue; // response skipped this item — FinalizeKeywordFlags handles it
                }

                state.Adjudicated = true;

                // Keyword-flagged: the LLM adjudicates. An explicit halal verdict overturns the
                // flag (context understood: "Barber shop", "hotel near the bar district"); a
                // confirming one keeps the removal, now carrying the model's confidence/reason.
                if (!verdict.IsHaram)
                {
                    _log?.Invoke($"moderation: LLM overturned keyword flag '{kw.Term}' ({kw.Category}) — kept item");
                    continue;
                }

                state.Removal = new FilterFinding(
                    verdict.Category ?? kw.Category, verdict.Reason ?? kw.Term, kind, state.Text, kw.Field,
                    null, null, verdict.Confidence, FindingSource.Llm, null, ItemRemoved: true);
            }
            else if (verdict is { IsHaram: true, Category: not null }
                && verdict.Confidence >= _policy.ThresholdFor(verdict.Category))
            {
                // LLM-only flag: context-aware catch the keyword list missed. Gated by the
                // per-category threshold learned from admin correct/incorrect ratings.
                state.Removal = new FilterFinding(
                    verdict.Category, verdict.Reason ?? "llm", kind, state.Text, Field: null,
                    null, null, verdict.Confidence, FindingSource.Llm, null, ItemRemoved: true);
            }
        }
    }

    /// <summary>
    /// Turns any keyword flag the LLM never adjudicated into a removal — the keyword-only path,
    /// and the fail-safe when the LLM call failed or its response skipped an item.
    /// </summary>
    private static void FinalizeKeywordFlags<T>(List<ItemState<T>> states, string kind)
    {
        foreach (var state in states)
        {
            if (state is { KeywordMatch: { } kw, Removal: null, Whitelisted: false, Adjudicated: false })
            {
                state.Removal = new FilterFinding(
                    kw.Category, kw.Term, kind, state.Text, kw.Field,
                    null, null, 1.0, FindingSource.Keyword, null, ItemRemoved: true);
            }
        }
    }

    private async Task<List<T>> ModerateImagesAsync<T>(
        List<T> kept, ModerationProjection<T> projection, List<ItemState<T>> states, CancellationToken ct)
    {
        var whitelistedImages = states.Where(s => s.Whitelisted && s.ImageUrl is not null)
            .Select(s => s.ImageUrl!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Distinct image URLs from kept, non-whitelisted items, capped by the shared run budget.
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in kept)
        {
            var url = projection.ImageUrl?.Invoke(item);
            if (string.IsNullOrWhiteSpace(url) || whitelistedImages.Contains(url) || !seen.Add(url))
            {
                continue;
            }

            if (_filter.IsWhitelisted(null, url, null))
            {
                continue;
            }

            urls.Add(url);
        }

        // Claim from the shared per-run budget atomically: like ContentFilter, one moderator can be
        // shared by reference across parallel sub-workflows.
        int take;
        while (true)
        {
            var current = Volatile.Read(ref _imageBudget);
            take = Math.Min(current, urls.Count);
            if (Interlocked.CompareExchange(ref _imageBudget, current - take, current) == current)
            {
                break;
            }
        }

        if (urls.Count > take)
        {
            _log?.Invoke($"moderation: image screening capped at {take} of {urls.Count} images this run");
            urls = urls.Take(take).ToList();
        }

        if (urls.Count == 0)
        {
            return kept;
        }

        IReadOnlyList<ImageVerdict> verdicts;
        try
        {
            verdicts = await _imageClassifier!.ClassifyAsync(urls, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return kept; // best-effort: a failed vision pass leaves images untouched
        }

        var flagged = verdicts
            .Where(v => v.IsHaram
                && v.Category is not null
                && HalalPolicy.AllowedCategories.Contains(v.Category)
                && !HalalPolicy.NeverFiltered.Contains(v.Category)
                && v.Confidence >= _policy.ThresholdFor(v.Category))
            .ToDictionary(v => v.ImageUrl, StringComparer.OrdinalIgnoreCase);

        if (flagged.Count == 0)
        {
            return kept;
        }

        // Strip the flagged IMAGE but keep the item — granular by design. One finding per item
        // occurrence so the admin sees each affected listing with its source link.
        var result = new List<T>(kept.Count);
        foreach (var item in kept)
        {
            var url = projection.ImageUrl?.Invoke(item);
            if (url is not null && flagged.TryGetValue(url, out var verdict))
            {
                _filter.RecordFinding(new FilterFinding(
                    verdict.Category!, verdict.Reason ?? "vision", projection.Kind,
                    projection.JoinedText(item), "image",
                    projection.SourceUrl?.Invoke(item), url,
                    verdict.Confidence, FindingSource.Vision,
                    ModerationKeys.HashContent(projection.JoinedText(item)), ItemRemoved: false));
                result.Add(projection.WithImageUrl!(item, null));
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>Per-item working state while the moderation stages run.</summary>
    private sealed class ItemState<T>
    {
        public ItemState(T item, IReadOnlyList<(string Field, string? Text)> fields, string text,
            string contentHash, string? sourceUrl, string? imageUrl)
        {
            Item = item;
            Fields = fields;
            Text = text;
            ContentHash = contentHash;
            SourceUrl = sourceUrl;
            ImageUrl = imageUrl;
        }

        public T Item { get; }
        public IReadOnlyList<(string Field, string? Text)> Fields { get; }
        public string Text { get; }
        public string ContentHash { get; }
        public string? SourceUrl { get; }
        public string? ImageUrl { get; }
        public bool Whitelisted { get; set; }
        public (string Category, string Term, string Field)? KeywordMatch { get; set; }
        public FilterFinding? Removal { get; set; }

        /// <summary>True once the LLM explicitly ruled on this keyword flag (confirm OR overturn).</summary>
        public bool Adjudicated { get; set; }
    }
}
