# Daleel — Architecture

## Goals

Daleel monitors Arabic-language social media for keyword hits. Two properties drive
the design:

1. **Matching must be robust to Arabic orthographic variation.** This is the core
   differentiator and lives in `Daleel.Core/Arabic`.
2. **Sources are pluggable.** Today it's Facebook via Apify actors; the pipeline must
   not care where posts come from.

## Layering (clean architecture)

Dependencies point inward. The domain core has zero external dependencies; everything
else depends on it, never the reverse.

```
            ┌───────────────────────────────────────────┐
            │                Daleel.Cli                  │
            │   System.CommandLine — search / monitor /  │
            │   test-match / dry-run                     │
            └───────────────┬───────────────────────────┘
                            │ depends on
        ┌───────────────────┼────────────────────┐
        ▼                   ▼                     ▼
┌───────────────┐  ┌─────────────────┐   ┌──────────────────┐
│ Daleel.Apify  │  │ Daleel.Pipeline │   │   (composition)  │
│  REST client  │  │  orchestration  │   └──────────────────┘
│  builders,    │  │  dedup, JSONL   │
│  mappers      │  └────────┬────────┘
└───────┬───────┘           │
        │  both depend on   │
        ▼                   ▼
        ┌───────────────────────────────┐
        │          Daleel.Core          │
        │  Models, Arabic engine,       │
        │  pipeline interfaces          │
        │  (no external dependencies)   │
        └───────────────────────────────┘
```

### Why interfaces live in Core

`IPostFetcher`, `IPostMatcher`, `IResultWriter`, and `IPipeline` are all defined in
`Daleel.Core/Pipeline`. The outer layers *implement* them. This is the Dependency
Inversion Principle: `Daleel.Pipeline` orchestrates against abstractions, so it can be
unit-tested with a fake fetcher and an in-memory writer while still exercising the real
Arabic matcher (see `MonitoringPipelineTests`).

## Data flow (a single run)

```
MonitoringJob (keywords, sources, mode)
      │
      ▼
for each Source:
      │
      ▼
  IPostFetcher.FetchAsync ───────────►  ApifyPostFetcher
      │                                   │
      │                                   ├─ pick input builder by SourceKind
      │                                   │    Search → FacebookSearchBuilder
      │                                   │    Group/Page → FacebookGroupBuilder
      │                                   ├─ ApifyClient.RunActorAndGetItemsAsync
      │                                   │    POST /v2/acts/{id}/runs
      │                                   │    poll GET /v2/actor-runs/{runId}
      │                                   │    GET  /v2/datasets/{dsId}/items
      │                                   └─ ApifyPostMapper.MapMany → SocialPost[]
      ▼
  IPostMatcher.Match  ───────────────►  ArabicMatcher
      │                                   ├─ ArabicNormalizer.Normalize(text, keyword)
      │                                   └─ Exact / Contains / Fuzzy(Levenshtein)
      │  (only matches continue)
      ▼
  PostDeduplicator.IsUnique ─────────►  SHA-256 of normalized text
      │  (only first sighting continues)
      ▼
  IResultWriter.WriteAsync ──────────►  JsonlResultWriter (one JSON object per line)
      │
      ▼
  PipelineReport { sources, fetched, duplicates, matches }
```

## The Arabic engine

The normalization pipeline is intentionally ordered:

1. **NFC** first so combining sequences are in a predictable, composed form.
2. **Strip diacritics** before letter folding, so a fatha on an alef-with-hamza does
   not interfere with the alef fold.
3. **Fold letters** (alef/maksura/taa-marbuta/hamza carriers), **drop tatweel**,
   **fold digits**.
4. **Collapse whitespace** last.

`Normalize` is **idempotent** — normalizing an already-normalized string is a no-op —
which is what makes it safe to hash for deduplication and to compare repeatedly.

### Matching modes

| Mode      | Rule                                                              | Score        |
|-----------|------------------------------------------------------------------|--------------|
| Exact     | normalized keyword == normalized text                            | 1.0          |
| Contains  | normalized text contains normalized keyword                      | 1.0          |
| Fuzzy     | a text token within `threshold` edit-distance of the keyword     | similarity   |

Fuzzy distance is normalized by the longer of (token, keyword) length, so the same
absolute edit count is stricter on short words than long ones.

## Deduplication

`PostDeduplicator` hashes each post's **normalized** text with SHA-256 and keeps a set
of seen hashes for the lifetime of a run. Because it hashes the normalized form,
cross-posted copies that differ only in diacritics or alef/hamza spelling collapse to
one hash and are dropped after the first sighting. The pipeline dedupes **after**
matching, so the hash set only ever holds posts we actually care about.

## Apify integration notes

- Actor ids contain a `/` (e.g. `apify/facebook-groups-scraper`); the API path encodes
  it as `~`.
- Actor input schemas vary, so input builders emit several common key aliases
  (`maxItems` / `maxPosts` / `resultsLimit`) and accept a full override JSON.
- Actor output fields vary too, so `ApifyPostMapper` probes a prioritized list of
  candidate keys (`text` / `message` / `message_rich` / `postText` / `content` / …)
  per field. This keeps the integration resilient to actor swaps and schema drift.
- Transient failures (5xx, 429, request timeouts) are retried with exponential backoff;
  run polling has an overall timeout.

## Testing strategy

- **Core** — unit tests for `DiacriticStripper`, `ArabicNormalizer`, `ArabicMatcher`
  (including all spec cases), and `PostDeduplicator`.
- **Pipeline** — integration tests wiring a fake fetcher + in-memory/JSONL writer
  through the *real* `ArabicMatcher`, verifying matching, dedup, and output shape.

External I/O (`ApifyClient` HTTP, real network) is kept behind interfaces so the
deterministic logic is fully testable offline.
