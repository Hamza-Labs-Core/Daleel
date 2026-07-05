# Provider & Worker Audit

**Date:** 2026-07-05 · **Branch:** `feature/cf-execution-phase0` (PR #39)
**Rule being audited:** every external call flows through a monitored path — the agent's
`LoggingProviders`/`LoggingLlmClient` decorators (wired in `AgentFactory.Build`) or the
`IProviderApi` gateway (`src/Daleel.Web/Services/ProviderApi.cs`). Direct provider construction is
allowed ONLY in `AgentFactory`, `ProviderApi` itself, and the CLI composition root.

## 1. .NET providers

| Provider | Interface(s) | Used for | Metered via | Status | Enabled by |
|---|---|---|---|---|---|
| **SerpApiProvider** | `ISearchProvider` | Web/Shopping/Maps/News/Images gather (`AgentService.Gather`) | `LoggingProviders.Wrap` | **Active** (primary search) | `SERPAPI_KEY` (or search-worker proxy with the key on the edge) |
| **GooglePlacesProvider** | `IPlacesProvider` | Store discovery/verification, details, reviews | `LoggingProviders.Wrap` (agent) · `IProviderApi.SearchPlacesAsync/GetPlaceDetailsAsync` (profile researcher) | **Active** | `GOOGLE_PLACES_API_KEY` (or proxy) |
| **ContextDevProvider** | `IScrapeProvider`, `IExtractProvider` | Page scrapes, brand intelligence, catalogue extraction | `LoggingProviders.WrapScrape` (agent) · `IProviderApi` (everything else) | **Active** (primary scraper) | `CONTEXT_DEV_API_KEY` |
| **CloudflareBrowserProvider** | `IScrapeProvider` | Fallback renderer in the `ScrapeRouter` chain (JS-heavy/anti-bot pages, browser-price harvest) | `LoggingProviders.WrapScrape` (wraps the whole router) | **Active as fallback** — reached when Context.dev fails/empties | `CLOUDFLARE_ACCOUNT_ID` + `CLOUDFLARE_API_TOKEN` |
| **BingProvider** | `ISearchProvider` | Web/News fallback search | `LoggingProviders.Wrap` | **Conditional — rarely reachable** (see note) | `BING_SEARCH_KEY` AND no `SERPAPI_KEY` AND no search proxy |
| **ApifyPostFetcher** (+`ApifyClient`) | `IPostFetcher` | Social posts (gather) + keyword monitors | `LoggingProviders.Wrap` (agent) · `IProviderApi.FetchSocialPostsAsync` (MonitorService) | **Active** | `APIFY_TOKEN` |
| **OpenRouterClient** | `ILlmClient` | Planner, analyst, extraction, moderation LLM adjudication, opinions, vision | `LoggingLlmClient` | **Active** (primary LLM) | `OPENROUTER_API_KEY` |
| **AnthropicClient** | `ILlmClient` | Same roles, direct-API fallback | `LoggingLlmClient` | **Conditional fallback** | `ANTHROPIC_API_KEY` and no OpenRouter key |
| **OpenAiClient** | `ILlmClient` | Same roles, fallback | `LoggingLlmClient` | **Conditional fallback** | `OPENAI_API_KEY`, later in `BuildLlm` order |
| **ScrapeRouter** | `IScrapeProvider` | Chains Context.dev → Cloudflare Browser with fail-over | wrapped as one unit | **Active** | ≥2 scrape backends configured |

**Bing note (deliberate):** when the search-worker proxy is configured, the edge-proxied
SerpApiProvider branch precedes Bing in `AgentFactory.Build` — SerpAPI capability via the proxy is
strictly better coverage. Consequence: the proxy's `SERPAPI_KEY` **worker secret must be set** or
searches fail while a Bing key sits unused. Bing remains the no-SerpAPI-anywhere fallback.

**CLI (`Daleel.Cli/Composition.cs`):** constructs providers directly and unmetered — accepted:
it is a developer tool with no job context, no cost cap, and no usage dashboard.

**Known metering-fidelity gap (accepted):** monitor social fetches now meter with env-resolved
keys; the previous per-user key override for monitor runs was dropped (the gateway is env-keyed;
agent builds keep two-tier user-key resolution).

## 2. Cloudflare worker fleet

| Worker | Role (doc §) | Production consumers today | Status | Enabled by |
|---|---|---|---|---|
| **scrape-worker** | Catalogue/brand crawls, async + R2-durable (§3.1) | `ScrapePricesActivity` → `IProviderApi.SubmitEdgeCatalogAsync`; results persisted by `CloudflarePollDrainService` | **Wired, flag-gated** | `CF_SCRAPE_WORKER_URL/TOKEN` + queue creds + R2 + `cloudflare.execution.enabled` |
| **search-worker** | KV-cached proxy for SerpAPI/Places (§3.5) | `AgentFactory.EdgeSearchClient` (agent providers) + `ProviderApi.SearchProxyClient` (gateway Places) | **Wired** — zero behavior change (Axis A) | `CF_SEARCH_WORKER_URL/TOKEN` |
| **classify-worker** | Commodity labeling on Workers AI (§3.2) | none — `IProviderApi.ClassifyTextAsync` has zero production callers | **Dormant (deliberate)** | `CF_CLASSIFY_WORKER_URL/TOKEN` + a consumer |
| **extract-worker** | Content → product JSON on Workers AI (§3.3) | none — `IProviderApi.ExtractProductsFromContentAsync` unreached | **Dormant (deliberate)** | `CF_EXTRACT_WORKER_URL/TOKEN` + a consumer |
| **filter-worker** | Halal findings-only signals (§3.4) | none — `FilterTextFindingsAsync`/`FilterImageFindingsAsync` unreached | **Dormant — A/B-gated** | `CF_FILTER_WORKER_URL/TOKEN` + the Phase-3 A/B |
| **log-viewer** | Admin read view over `daleel-logs` R2 | admin panel | **Active** | `LOG_VIEWER_URL/AUTH_TOKEN` |

Dormant worker **endpoints** on wired workers: scrape-worker `/scrape/page` and `/scrape/brand`
(only `/scrape/catalog` is submitted today); extract-worker `/extract/structured`.

Every fleet capability is reachable **only** through `IProviderApi`, so the first consumer of each
is metered on day one (`workers-ai/*` provider names in the usage log).

## 3. Why the dormant three are dormant — and their activation paths

These are **Phase 2/3 of the architecture doc's own migration plan (§6)**, not oversights. Each
routes provider work onto *new models* (Workers AI), which is Axis B — the doc mandates
shadow-compare/A-B validation before defaults flip. Concrete next consumers, by effort:

| Capability | Best first consumer (seam) | Effort | Risk / gate |
|---|---|---|---|
| **extract-worker** | Browser-price fallback: when `ItemEnrichmentService.HarvestViaBrowserAsync` has rendered markdown but regex price-parsing found nothing, feed the markdown to `ExtractProductsFromContentAsync` and merge structured products (additive — can only add results) | **S** | Low — additive, fail-open |
| **classify-worker** | Listing condition labeling (new/used/refurb) where `NormalizeCondition`'s keyword match returns null; later: pre-gate for the LLM relevance gate to cut its input size | **S–M** | Low–medium — verdicts advisory |
| **filter-worker** | **Shadow mode** inside `HalalModerator`: call `FilterTextFindingsAsync` alongside `LlmHalalClassifier` (`HalalModerator.cs` stage 3 seam), log agreement/divergence to `FilteredContentLog` — flip nothing | **M** | Moderation precision is a tracked metric; the shadow IS the A/B the doc requires. Worker-side guarantees already in place: riba can never be emitted; llama-guard confidence pinned below removal thresholds |
| **scrape-worker `/scrape/brand`** | `BrandCatalogService.HarvestAsync` submit-and-forget (mirror of the store-catalogue path, drain persists `BrandModel`s) | **M** | Needs a brand drain handler |

## 4. Resource provisioning (CI check-or-create)

`workers/provision.sh <worker> <prod|qa>` — idempotent, run by the fleet deploy workflow before
every deploy and usable manually:

| Worker | Resources ensured | Mechanism |
|---|---|---|
| scrape-worker | `daleel[-qa]-{scrape-work, scrape-dlq, poll-work, poll-dlq}` queues + HTTP pull consumer on poll-work (`--message-retries 30`, DLQ) | `wrangler queues info || create`; consumer-add tolerant of already-attached |
| search-worker | `daleel[-qa]-search-cache` KV namespaces | resolved by title via `wrangler kv namespace list`, created when missing, id injected into `wrangler.jsonc` placeholders |
| classify/extract/filter | none (account-level AI binding) | no-op |
| log-viewer | none (`daleel-logs` bucket is app-managed) | no-op |

All worker configs are **`wrangler.jsonc`** (comments preserved; TOML retired).

## 5. Keeping this audit honest

New provider capability ⇒ add it to `IProviderApi` (metered by construction), never a direct
construction at a call site. New worker ⇒ fleet matrix entry + `provision.sh` case + this table.
The grep that should stay clean outside `AgentFactory`/`ProviderApi`/CLI:
`grep -rn "new \(ContextDevProvider\|SerpApiProvider\|GooglePlacesProvider\|BingProvider\|CloudflareBrowserProvider\|ApifyPostFetcher\)" src/`
