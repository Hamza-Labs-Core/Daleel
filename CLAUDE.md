# Daleel — agent instructions

Daleel is an AI shopping assistant for MENA markets (default: Jordan): a .NET 8 **Blazor Server** app
(`src/Daleel.Web`) over an LLM-driven search pipeline (Elsa workflows) that discovers, scrapes,
extracts and enriches product data from local stores. Arabic + English throughout.

## Build & test — the deploy gate

- CI builds **`-c Release -warnaserror`** with the MudBlazor analyzer as errors (MUD0002). Local Debug
  misses these — always run `dotnet build Daleel.sln -c Release -warnaserror` before pushing razor changes.
- `dotnet test` takes ONE project per invocation. Suites: `Daleel.Core.Tests`, `Daleel.Agent.Tests`,
  `Daleel.Search.Tests`, `Daleel.Pipeline.Tests`, `Daleel.Web.Tests` (bUnit lives here). E2E tests skip
  without a live server.
- Dockerfile's csproj COPY list must match `Daleel.sln`; new compose services need env wired into
  `deploy.yml`'s `.env` render — deploy-only breakages hide here.

## Branching & QA workflow

- **Integration-branch rule:** when several feature PRs are in flight, cut ONE integration branch off
  `main`, PR every feature INTO it, put the `qa` label ONLY on it, merge it to `main` once at the end.
  There is a single QA server (`qa-daleel.hamzalabs.dev`) — two independent `qa`-labeled PRs
  leapfrog-revert each other's features on every push.
- Merging a BASE branch auto-closes PRs that target it — the integration branch merges to main LAST.
  Update stacked branches by merging `main` in, never rebasing.
- "Done" for a qa-labeled branch means: push → auto-deploy → **verify on QA**, not local tests green.
- QA and PROD share R2 buckets but have separate hosts/DBs; QA deploys from main pushes + the `qa`
  PR label (opt-in preview); PROD is separate (`latest` tag, DEPLOY_SSH_HOST).

## Architecture invariants (do not violate)

- **Blazor Server-interactive ONLY** — never Auto/WASM render modes (pages inject server-only DI).
  Navigation into `/login`/`/register` must be a full page load (`forceLoad`/`data-enhance-nav="false"`).
- **DbContext is transient** + repositories — a scoped context shared across a circuit crashes under
  Postgres ("command already in progress").
- **Entity storage:** rich search entities are self-contained JSON docs in R2 (source of truth);
  Postgres holds only index + FKs. Never add typed attribute columns for entity data.
- **NO user-supplied API keys.** Server env is the only key source. Never reintroduce key parameters.
- **No domain guessing.** Trust order for a brand/store site: saved Website → Places → LLM actor.
  Never synthesize domains from names.

## Pipeline invariants

- **Best-effort everywhere:** an enrichment/crawl/LLM failure must degrade, never fault the search.
  Hot-path DB reads (e.g. cancel checks) must be non-fatal. An empty grid is the worst outcome —
  the relevance gate deliberately distrusts drop-everything verdicts on small sets.
- **Every page-fetch path needs the FULL provider fallback chain** (Context.dev → Cloudflare Browser
  Rendering). A context.dev-only path dies silently for weeks when credits deplete — this killed all
  `enrich.verifypage` units once (117 dead, 0 completed) and every crawled card lost its price+image.
  When adding a fetch site, mirror `ScrapeRouter` / `ProviderApi.ScrapePageAsync`.
- **CF Browser /json contract:** `response_format` accepts ONLY type `"json_schema"` with a BARE
  schema body; schema-less mode = omit `response_format` and pass a prompt. `"json"` 422s.
- **Streaming identity rule:** partial results push a NEW `ProductSearchResult` reference every ~800ms
  for the SAME search. Grid state that seeds from the result (sort, facet selections) must key on
  SEARCH IDENTITY (Query+Geo), never on reference change, or user choices reset mid-run.
- **The search object:** `SearchStrategy` carries the parsed query (Product/Specs/Location/Goal/
  Facets/DefaultSort) and is stamped onto `ProductSearchResult.Strategy` at ALL `AgentAnswer`
  construction sites (agent, aggregate, salvage — salvage also feeds partial pushes). All fields are
  additive-optional: strategy-less results must render exactly as before.
- **Store search boxes AND-match:** strip geo/filler tokens (via `QueryScope`) from anything typed
  into a store's own search ("diapers Jordan" returns the store's no-results page).
- **Enrichment work queue** (`EnrichmentWorkItems`) IS the queue: per-unit retry + dead ledger, no
  watchdogs, patches compose under the job row lock. LLM planner JSON must be parsed null-tolerantly
  (`JsonElement` + coercion for spec values — one bad type must not discard the whole plan).

## Localization

- Every new `L["Key"]` needs the key in BOTH `SharedResource.resx` AND `SharedResource.ar.resx` (parity).
- Normalization helpers must stay Unicode-aware (`char.IsLetterOrDigit` keeps Arabic; never "optimize"
  to ASCII). RTL text direction via `Catalog.Dir(text)`.

## Debugging QA

- `/admin/workflows` → click a run for its step timeline (event-name histogram reveals which stage
  produced/lost data). `/admin/queues` → dead-unit ledger with reason strings (grep the reason string
  into the handler). `/admin/timeline` is the unified system-event feed.
- The log-viewer worker reads PROD logs only; QA needs SSH docker logs (1Password-gated) or the admin UI.
- UI automation: the Playwright MCP admin profile drives MudBlazor; CDP-synthetic typing does NOT bind
  Blazor server state (clicks do). `/?Q=<query>` pre-fills the search box only on a HARD page load.
- "No results just yet" can be a FAULTED run, not an empty one — check the run status server-side.
