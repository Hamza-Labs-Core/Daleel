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

**Metering fidelity:** monitor social fetches keep the two-tier key resolution — the gateway's
`FetchSocialPostsAsync`/`HasSocial` accept the caller's key dict, so a user-supplied APIFY_TOKEN
still wins over the server's (the earlier regression is fixed).

## 2. Cloudflare worker fleet

| Worker | Role (doc §) | Production consumers today | Status | Enabled by |
|---|---|---|---|---|
| **scrape-worker** | Catalogue/brand crawls, async + R2-durable (§3.1) | `ScrapePricesActivity` → `IProviderApi.SubmitEdgeCatalogAsync`; results persisted by `CloudflarePollDrainService` | **Wired, flag-gated** | `CF_SCRAPE_WORKER_URL/TOKEN` + queue creds + R2 + `cloudflare.execution.enabled` |
| **search-worker** | KV-cached proxy for SerpAPI/Places (§3.5) | `AgentFactory.EdgeSearchClient` (agent providers) + `ProviderApi.SearchProxyClient` (gateway Places) | **Wired** — zero behavior change (Axis A) | `CF_SEARCH_WORKER_URL/TOKEN` |
| **classify-worker** | Commodity labeling on Workers AI (§3.2) | `ItemEnrichmentService.BackfillConditionsAsync` — condition (used/refurbished) labeling for offers carrying none | **Wired** | `CF_CLASSIFY_WORKER_URL/TOKEN` |
| **extract-worker** | Content → product JSON on Workers AI (§3.3) | Browser-price fallback in `HarvestViaBrowserAsync` — rendered markdown → structured products when regex parsing finds nothing | **Wired** | `CF_EXTRACT_WORKER_URL/TOKEN` |
| **filter-worker** | Halal findings-only signals (§3.4) | `ShadowHalalClassifier`/`ShadowHalalImageClassifier` — detached A/B shadow on every moderation batch (`halal-shadow` log lines are the comparison dataset) | **Wired as SHADOW** — default routing still A/B-gated | `CF_FILTER_WORKER_URL/TOKEN`; flipping defaults still requires the accumulated A/B evidence |
| **log-viewer** | Admin read view over `daleel-logs` R2 | admin panel | **Active** | `LOG_VIEWER_URL/AUTH_TOKEN` |

Also wired since the first audit pass: scrape-worker `/scrape/page` (the gateway's
`ScrapePageAsync` prefers the edge and degrades inline) and `/scrape/brand`
(`BrandCatalogService.HarvestAsync` submit-and-forget; the drain's brand handler persists the
`BrandModel` rows). The single remaining consumer-less endpoint is extract-worker
`/extract/structured` — the generic schema surface that /extract/products is a specialization of;
it gains callers when schema-driven extraction cases arrive, and adding one goes through
`IProviderApi` (metered on day one).

Every fleet capability is reachable **only** through `IProviderApi`, so the first consumer of each
is metered on day one (`workers-ai/*` provider names in the usage log).

## 3. Formerly-dormant capabilities — now wired (2026-07-05)

Every capability from the first audit pass now has a production consumer; all remain **fail-open
and advisory** so a dead edge host changes nothing:

| Capability | Consumer (implemented) | Behavior |
|---|---|---|
| **extract-worker** | Browser-price fallback (`ItemEnrichmentService.HarvestViaBrowserAsync`) | Fires only when regex parsing of a rendered page found nothing; extracted priced products become browser-price observations. Strictly additive |
| **classify-worker** | Condition backfill (`ItemEnrichmentService.BackfillConditionsAsync`, Phase 7) | Models whose offers all lack a condition get used/refurbished labels — only high-confidence non-"new" verdicts apply |
| **filter-worker** | `ShadowHalalClassifier` + `ShadowHalalImageClassifier` (wrapped in `AgentFactory.Build` when the host is configured) | Detached shadow per moderation batch; `halal-shadow` log lines carry agreement/divergence — the A/B dataset. Inner classifiers stay authoritative; **default routing flips only on accumulated evidence** |
| **scrape-worker `/scrape/brand`** | `BrandCatalogService.HarvestAsync` edge path + the drain's brand handler | Submit-and-forget; drain upserts the `BrandModel` rows whenever the crawl lands |
| **scrape-worker `/scrape/page`** | `IProviderApi.ScrapePageAsync` edge preference | Edge first (key off the VPS), inline Context.dev fallback |

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
