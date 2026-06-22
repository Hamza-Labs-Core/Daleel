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

---

# The intelligence layer (agent + search)

The monitoring pipeline above answers "did anyone mention keyword X?". The agent layer
answers open questions like "what's the best AC in Jordan?" by making the LLM both the
**query planner** and the **analyst**, with concrete providers doing the data gathering in
between.

## Dependency direction

`ILlmClient` lives in `Daleel.Core` (a domain abstraction), so `Daleel.Pipeline`'s
LLM-powered `OpinionExtractor` can depend on it without a cycle. The concrete LLM clients
(`OpenAiClient`, `AnthropicClient`) live in `Daleel.Agent`. `Daleel.Agent` references Core,
Search, Apify, and Pipeline; nothing references Agent except the CLI. This keeps the graph
acyclic while satisfying "LLM provider implementations live in the Agent project".

## Agent flow

```
question + geo
      │
      ▼
LLM PLANNER  (PromptTemplates.Plan*  →  SearchStrategy JSON)
      │   classifies QueryType, emits bilingual web/shopping/social/places queries
      ▼
GATHER (parallel, failure-isolated)
   ├─ ISearchProvider   (SerpAPI / Bing)      → web + shopping results
   ├─ IPlacesProvider   (Google Places)       → store locations + reviews
   ├─ IPostFetcher      (Apify)               → social posts → Arabic matcher filters
   └─ IScrapeProvider   (Context.dev→CF)      → deep-read specific URLs
      │
      ▼
ANALYZE & PROJECT
   ├─ OpinionExtractor (LLM)  → structured CustomerOpinion[]
   ├─ PriceTracker / MarketplaceAggregator → price points + comparison
   ├─ DealScorer → ranked deals
   └─ LLM ANALYST (PromptTemplates.Analyze) → narrative summary
      │
      ▼
  BrandReport / ProductIntelligence / AgentAnswer / StoreLocation[]
```

## Provider design

All HTTP providers extend `HttpProviderBase` (injectable `HttpClient` + retry/backoff with
an injectable delay), so every provider is testable with a stub `HttpMessageHandler` and no
real waits. Providers normalize their wire formats into shared shapes (`SearchResult`,
`StoreLocation`, `ScrapedPage`, `PricePoint`) so the agent never sees a provider's raw JSON.

- **SerpAPI** (`SerpApiProvider`) — primary search; one key covers Google Web/Shopping/Maps,
  geo via `gl`/`hl`.
- **Bing** (`BingProvider`) — web/news fallback; market code `mkt` (e.g. `ar-JO`).
- **Google Shopping** (`GoogleShoppingProvider`) — composes over any shopping-capable
  `ISearchProvider`, emitting `PricePoint`s.
- **OpenSooq** (`OpenSooqProvider`) — Jordan/Gulf classifieds; JS-heavy + anti-bot, so it
  scrapes via an `IScrapeProvider` and extracts listings from markdown (price taken from the
  text *after* each listing link, so a "24000 BTU" title isn't mistaken for a price).
- **Google Places** (`GooglePlacesProvider`) — text/nearby search, details, reviews; computes
  distance locally with haversine.
- **Context.dev** (`ContextDevProvider`) — primary scraper: scrape→markdown, AI extract
  against a schema, brand enrichment, crawl. (Replaced the earlier Firecrawl integration.)
- **Cloudflare Browser** (`CloudflareBrowserProvider`) — fallback scraper; edge-rendered
  headless browser for the toughest pages, plus screenshots and custom JS.
- **`ScrapeRouter`** chains scrapers (Context.dev → Cloudflare), falling through on empty/error.

## Geo profiles

`GeoProfile` encodes "how to research this country": language priority (drives bilingual
query generation), currency (drives price defaults), local social platforms + Apify actors,
marketplaces, and a city-center `GeoPoint` for proximity search. `GeoProfiles` ships
`jordan`, `saudi`, `uae`, `egypt`, `usa` and resolves by key, ISO code, country, or city.

## Robustness choices

- **Planner output is parsed leniently** (`LlmJson`) — code fences and surrounding prose are
  tolerated; unparseable output yields an empty strategy rather than a crash.
- **Every gather call is wrapped** — a failing or unconfigured provider contributes nothing
  instead of failing the run.
- **Clocks and delays are injected** — `AgentOptions.Clock`, provider `delay` — so timing is
  deterministic in tests.

## Testing strategy (intelligence layer)

- **Core** — `PriceParser` (currencies, Arabic digits, separators), `GeoProfiles`, `LlmJson`.
- **Search** — `MarketplaceAggregator`, `OpenSooqProvider` extraction (fake scraper),
  `GooglePlacesProvider` parsing + haversine (stub handler), `SerpApiProvider` parsing +
  geo params (stub handler), `ScrapeRouter` fallback.
- **Pipeline** — `DealScorer`, `PriceTracker`, `OpinionExtractor` (fake LLM).
- **Agent** — `AgentService` plan→gather→analyze with a fake LLM (routed by system prompt)
  and fake search; `AnthropicClient`/`OpenAiClient` response parsing (stub handler).

No test touches the network; 159 tests run in well under a second.
