# Daleel (دليل)

[![CI](https://github.com/Hamza-Labs-Core/Daleel/actions/workflows/ci.yml/badge.svg)](https://github.com/Hamza-Labs-Core/Daleel/actions/workflows/ci.yml)
[![Deploy](https://github.com/Hamza-Labs-Core/Daleel/actions/workflows/deploy.yml/badge.svg)](https://github.com/Hamza-Labs-Core/Daleel/actions/workflows/deploy.yml)

**Daleel** — Arabic for *"guide"* — is an **Arabic-first market & social-media intelligence
tool**. It answers questions like *"What's the best AC in Jordan?"* / *"أفضل مكيف في الأردن"*
by letting an LLM plan a bilingual research strategy, executing it across web search,
shopping/marketplaces, Google Places, and social platforms, filtering everything through
an Arabic-aware matcher, and synthesizing a decision-ready report.

The original keyword-monitoring core (Arabic normalization + Apify social scraping) is
still here — the LLM agent is layered **on top** of it. The normalizer does the matching;
the LLM drives the strategy and the analysis.

---

## What it does

- **Free-form questions** (Arabic or English): the agent classifies intent, plans searches
  in both languages, gathers results, and writes an answer with prices, stores, and sentiment.
- **Brand intelligence**: presence, stores, deals, social sentiment, competitors.
- **Product research**: top products, aggregated opinions, price ranges, where to buy.
- **Store finding**: Google Places lookup with contact info, hours, ratings, reviews, distance.
- **Deal hunting & price comparison** across marketplaces (Google Shopping, OpenSooq, …).
- **Keyword monitoring** of social platforms (the original pipeline), now geo-aware.

---

## Architecture

A .NET 8 solution, clean architecture — dependencies point inward toward the domain core.

```
            ┌──────────────────────────────┐
            │          Daleel.Cli          │  System.CommandLine
            └──────────────┬───────────────┘
        ┌──────────────────┼─────────────────────┐
        ▼                  ▼                      ▼
┌───────────────┐  ┌────────────────┐    ┌────────────────┐
│  Daleel.Agent │  │ Daleel.Pipeline│    │  Daleel.Apify  │
│  LLM planner  │  │  orchestration │    │  social scrape │
│  + analyst    │  │  dedup/price/  │    └───────┬────────┘
│               │  │  deal/opinion  │            │
└───┬───────┬───┘  └───────┬────────┘            │
    │       │              │                     │
    ▼       ▼              ▼                     ▼
┌────────────────┐   ┌──────────────────────────────────┐
│ Daleel.Search  │   │            Daleel.Core            │
│ serp/places/   │──►│  Arabic engine, models, geo,      │
│ shopping/scrape│   │  pricing, LLM abstraction (no deps)│
└────────────────┘   └──────────────────────────────────┘
```

| Project           | Responsibility |
|-------------------|----------------|
| `Daleel.Core`     | Arabic normalization/matching, domain + report models, geo profiles, price parsing, `ILlmClient` abstraction. No external deps. |
| `Daleel.Search`   | Search & scrape providers: SerpAPI, Bing, Google Shopping, Google Places, OpenSooq, Context.dev, Cloudflare Browser; marketplace aggregation. |
| `Daleel.Apify`    | Apify actor integration for social platforms (Facebook/Instagram/Twitter). |
| `Daleel.Pipeline` | Orchestration: monitoring pipeline, dedup, JSONL output, price tracking, deal scoring, LLM opinion extraction. |
| `Daleel.Agent`    | The intelligence engine: LLM clients (OpenRouter/OpenAI/Anthropic), prompt templates, `AgentService` (planner + analyst). |
| `Daleel.Cli`      | Console entry point. |
| `Daleel.Web`      | Blazor Web App (.NET 8, Interactive Server + WebAssembly) UI built on MudBlazor: Ask, Brand, Stores, Deals, Compare, Monitor, Settings. Bilingual (Arabic/English), RTL-aware, dark by default. |
| `Daleel.Web.Client` | WebAssembly companion for the Auto render mode. |

See [docs/architecture.md](docs/architecture.md) for the full design and data flow.

### How the agent works

The LLM is used **twice** per query:

1. **Planner** — turns the question + market into a JSON `SearchStrategy`: bilingual web /
   shopping / social / places queries, plus URLs worth deep-reading.
2. **Analyst** — reads everything gathered (search results, listings, store data, opinions)
   and writes the final report.

Between them, concrete providers run **in parallel** and the Arabic matcher filters social
noise. Every provider is an injected interface and every call is failure-isolated, so the
agent degrades gracefully when a provider isn't configured.

---

## Arabic normalization (the matching core)

`ArabicNormalizer.Normalize` collapses orthographic variants to one canonical form:

1. Unicode NFC → 2. strip diacritics/tashkeel → 3. fold alef variants (`أ إ آ ٱ → ا`) →
4. alef-maksura → yaa (`ى → ي`) → 5. taa-marbuta → haa (`ة → ه`) → 6. drop tatweel (`ـ`) →
7. fold hamza carriers (`ؤ → و`, `ئ → ي`) → 8. collapse whitespace + fold Arabic-Indic digits.

So `شَرِكَة` reliably matches `شركة`, `الشركه`, etc. Matching supports **exact**, **contains**,
and **fuzzy** (token-level Levenshtein) modes. The same normalizer powers price parsing
(Arabic-Indic digits) and content-hash deduplication.

---

## Build, test, run

Requires the **.NET 8 SDK**.

```bash
dotnet build          # build the solution (0 warnings)
dotnet test           # run all tests (xUnit + FluentAssertions + bUnit)
```

### Offline commands (no API keys)

```bash
# Test Arabic matching of a keyword against text
dotnet run --project src/Daleel.Cli -- test-match \
  --keyword "شَرِكَة" --text "هذا نص يتحدث عن شركة الاتصالات"

# Run the built-in normalization demo suite
dotnet run --project src/Daleel.Cli -- dry-run
```

### Agent commands (need an LLM key + provider keys)

```bash
daleel ask "What's the best AC in Jordan?"            # free-form, agent plans everything
daleel ask "أفضل مكيف في الأردن" --geo jordan          # same, in Arabic
daleel brand "ماكدونالدز" --geo jordan                 # brand intelligence report
daleel product "مكيف" --geo jordan                     # product-category research
daleel stores "مكيفات" --geo jordan --near "عمان"      # Google Places store finder
daleel nearby "electronics" --lat 31.95 --lng 35.93 --radius 5000
daleel deals "سامسونج" --geo jordan                    # current deals
daleel compare "Samsung AR24" "LG Dual Inverter" --geo jordan
daleel reviews "مكيف سبليت" --geo jordan               # aggregated opinions
```

### Social monitoring (the original pipeline)

```bash
daleel search --keyword "شركة الاتصالات" --actor scrapeforge/facebook-search-posts --max 25
daleel monitor --config sources.json                   # job from a config file
daleel monitor "اسم_الشركة" --geo jordan --interval 24h # keyword monitoring on a loop
```

### Web UI (Daleel.Web)

A polished Blazor front-end over the same `AgentService`. Dark by default, bilingual
(Arabic/English) with automatic RTL for Arabic content, responsive on mobile.

```bash
dotnet run --project src/Daleel.Web        # then open the printed https/http URL
```

Pages: **Ask** (`/`) free-form research with a Google-style box, **Brand** (`/brand`),
**Stores** (`/stores`, with browser geolocation), **Deals** (`/deals`), **Compare**
(`/compare`), **Monitor** (`/monitor`), and **Settings** (`/settings`).

Long-running agent queries stream live progress to the page over the Interactive Server
circuit. API keys can come from server environment variables **or** be entered on the
Settings page (stored in the browser's `localStorage` only, never written to server logs).

---

## API keys & setup

Daleel reads credentials from environment variables. Set only the ones for the capabilities
you want — the agent wires up whatever is available and skips the rest.

| Variable | Used for | Required for |
|----------|----------|--------------|
| `OPENROUTER_API_KEY` | OpenRouter — one key, **every** model (preferred LLM) | any agent command |
| `OPENAI_API_KEY` | OpenAI directly (LLM fallback) | any agent command (if no OpenRouter key) |
| `ANTHROPIC_API_KEY` | Anthropic Claude directly (LLM fallback) | any agent command (if no OpenRouter/OpenAI key) |
| `SERPAPI_KEY` | SerpAPI — Google Web/Shopping/Maps in one | web/shopping/maps search |
| `BING_SEARCH_KEY` | Bing Web Search (search fallback) | web/news search (if no SerpAPI) |
| `GOOGLE_PLACES_API_KEY` | Google Places API (New) | `stores`, `nearby`, store enrichment |
| `CONTEXT_DEV_API_KEY` | Context.dev — scrape→markdown, brand data, AI extract | deep-reading pages, brand enrichment |
| `CLOUDFLARE_ACCOUNT_ID` + `CLOUDFLARE_API_TOKEN` | Cloudflare Browser Rendering (scrape fallback) | rendering JS-heavy / anti-bot pages |
| `APIFY_TOKEN` | Apify actors | social monitoring (`search`, `monitor`), social fetch in agent |
| `POSTGRES_CONNECTION_STRING` _or_ `DATABASE_URL` | PostgreSQL pipeline **event store** (optional) | `/admin/usage` cost dashboard; unset ⇒ SQLite-only, store is a no-op |

```bash
export OPENROUTER_API_KEY=sk-or-...
export SERPAPI_KEY=...
export GOOGLE_PLACES_API_KEY=...
export CONTEXT_DEV_API_KEY=...
export CLOUDFLARE_ACCOUNT_ID=...   CLOUDFLARE_API_TOKEN=...
export APIFY_TOKEN=apify_...
```

### LLM provider: OpenRouter (recommended)

[OpenRouter](https://openrouter.ai) is an LLM gateway: a **single key** that reaches Claude,
GPT, Gemini, Llama, Mistral and hundreds of other models through one OpenAI-compatible API.
Daleel prefers it because you can switch models per command without juggling separate
provider accounts.

1. Sign up at **[openrouter.ai](https://openrouter.ai)** and add credit (pay-as-you-go).
2. Create an API key at **[openrouter.ai/keys](https://openrouter.ai/keys)**.
3. Export it: `export OPENROUTER_API_KEY=sk-or-...`

Pick the model per run with `--model` (alias `-m`); omit it to use the default
(`anthropic/claude-sonnet-4`):

```bash
daleel ask "أفضل مكيف في الأردن" --geo jordan --model anthropic/claude-sonnet-4
daleel product "مكيف" --geo jordan -m google/gemini-2.5-flash   # cheaper / faster
```

**Recommended models for Arabic intelligence work:**

| Model id | Why |
|----------|-----|
| `anthropic/claude-sonnet-4` | Strong Arabic comprehension & reasoning — the default. |
| `anthropic/claude-opus-4` | Highest quality for deep brand/competitor analysis. |
| `google/gemini-2.5-flash` | Fast and inexpensive; good for high-volume/batch runs. |
| `google/gemini-2.5-pro` | Solid Arabic with a very large context window. |
| `openai/gpt-4o` | Reliable bilingual fallback. |

Provider selection order is **OpenRouter → OpenAI → Anthropic**: if `OPENROUTER_API_KEY` is
set it wins. The direct `OPENAI_API_KEY` / `ANTHROPIC_API_KEY` paths remain as fallbacks for
anyone who prefers calling those providers natively. `--model` overrides the chosen
provider's default model (most useful with OpenRouter, where model ids are namespaced like
`anthropic/claude-sonnet-4`).

### Scraping priority

When the agent wants to deep-read a page, it routes through providers in order:

1. **SerpAPI** — structured search (web, shopping, maps)
2. **Context.dev** — page scraping, brand intelligence, structured extraction
3. **Cloudflare Browser** — fallback for pages needing custom JS execution / anti-bot
4. **Apify** — social-media platforms

---

## Markets (geo profiles)

`--geo` selects a pre-built market profile that configures languages, currency, local
social platforms, marketplaces, Apify actors, and a city center for proximity search:

`jordan` (Amman, Arabic-first, Facebook + OpenSooq) · `saudi` (Riyadh, Twitter-heavy) ·
`uae` (Dubai) · `egypt` (Cairo) · `usa` (New York, English).

---

## Output

Agent commands print the analyst's narrative followed by the full structured report as JSON
(Arabic written un-escaped). Monitoring writes matched posts as
[JSON Lines](https://jsonlines.org/) — one object per line, append-friendly and tailable.

---

## Project layout

```
Daleel/
├── Daleel.sln
├── sources.json                # example monitor config
├── src/
│   ├── Daleel.Core/            # Arabic engine, models, geo, pricing, LLM abstraction
│   ├── Daleel.Search/          # search + scrape providers, marketplace aggregation
│   ├── Daleel.Apify/           # social platform integration
│   ├── Daleel.Pipeline/        # orchestration, dedup, price/deal/opinion analysis
│   ├── Daleel.Agent/           # LLM clients, prompts, AgentService
│   ├── Daleel.Cli/             # console entry point
│   ├── Daleel.Web/             # Blazor Web App UI (MudBlazor, Interactive Server + WASM)
│   └── Daleel.Web.Client/      # WebAssembly companion (Auto render mode)
├── tests/                      # Core / Pipeline / Search / Agent / Web test suites
├── Dockerfile                  # multi-stage build -> ASP.NET 8 runtime (port 8080)
├── Makefile                    # build / test / docker / deploy / setup-vps shortcuts
├── deploy/                     # production compose, Caddy, VPS bootstrap + deploy scripts
└── docs/
    └── architecture.md
```

---

## Deployment

Production: **https://daleel.hamzalabs.dev**

Daleel ships as a single container image (`ghcr.io/hamza-labs-core/daleel`) running the
`Daleel.Web` Blazor app on port **8080** behind **Caddy** (automatic HTTPS via Let's
Encrypt). CI/CD is GitHub Actions; the target is a **Hetzner CX23** VPS (Ubuntu 22.04/24.04).

### Pipelines

| Workflow | Trigger | What it does |
|----------|---------|--------------|
| [`ci.yml`](.github/workflows/ci.yml) | push to `main`, PRs | `dotnet build -warnaserror` → `dotnet test` → (main only) build + push image to GHCR |
| [`deploy.yml`](.github/workflows/deploy.yml) | `workflow_dispatch`, tags `v*` | build + test → push image → **deploy to VPS** (manual approval via the `production` environment), with health-check + automatic rollback |

### VPS setup — automatic, no manual step

There is **no separate setup script to run**. The [`deploy.yml`](.github/workflows/deploy.yml)
workflow's **Bootstrap VPS** step SSHes into the box and provisions it in-place, idempotently:
it installs Docker + Compose, creates `/opt/daleel`, installs the `daleel.service` systemd unit,
opens UFW ports (22/80/443), configures Docker log rotation, and logs in to GHCR (using the
workflow's run-scoped `GITHUB_TOKEN`). On a fresh box it provisions everything; on an
already-provisioned box every check is a fast no-op. The deploy is a single self-contained action.

To stand up a brand-new server, you only need to:

1. Point DNS `daleel.hamzalabs.dev` (A/AAAA) → the server IP. To use a different host, set `CADDY_DOMAIN` in `.env`.
2. Make sure the required GitHub secrets are set (see below) — including `DEPLOY_SSH_HOST`,
   `DEPLOY_SSH_USER`, and `DEPLOY_SSH_KEY` for root SSH access to the box.
3. Run the deploy (push a `v*` tag or run the **Deploy** workflow) and approve the `production` gate.

The workflow writes `/opt/daleel/.env` from the GitHub secrets on every deploy, so you never
hand-edit it. (`deploy/setup.sh` is now just a stub that points here.)

### Deploying a new version

- **Automatic:** push a `v*` tag (or run the **Deploy** workflow manually) → approve the
  `production` gate → the runner bootstraps the box (idempotent) and runs `deploy.sh`.
- **Manual on the box:** `cd /opt/daleel && ./deploy.sh <tag>` — pulls the image, restarts
  with `--wait`, health-checks `/health`, and **rolls back to the previous image** on failure.

`make` shortcuts: `make build`, `make test`, `make docker`, `make deploy`.

### Required GitHub secrets

Create with `./deploy/create-secrets.sh` (seeds every secret to `CHANGE_ME`), then fill in
real values under **Settings → Secrets and variables → Actions**.

| Secret | Used by | Purpose |
|--------|---------|---------|
| `OPENROUTER_API_KEY` | app | LLM provider (preferred) |
| `SERPAPI_KEY` | app | SerpApi web/shopping search |
| `CONTEXT_DEV_API_KEY` | app | context.dev provider |
| `GOOGLE_PLACES_API_KEY` | app | Google Places lookups |
| `APIFY_TOKEN` | app | Apify social scraping actors |
| `DEPLOY_SSH_HOST` | `deploy.yml` | VPS hostname / IP |
| `DEPLOY_SSH_USER` | `deploy.yml` | SSH user for deployment |
| `DEPLOY_SSH_KEY` | `deploy.yml` | SSH private key for deployment |

> Pushing to GHCR uses the built-in `GITHUB_TOKEN` (`packages: write`) — no PAT needed.
> App secrets are injected at runtime via `/opt/daleel/.env`, **not** baked into the image.
>
> `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_API_TOKEN` are **not** per-repo secrets — they're
> provided at the GitHub org level. The app still reads them at runtime from `/opt/daleel/.env`
> (documented in [`deploy/.env.example`](deploy/.env.example)).
>
> The PostgreSQL **event store** (`POSTGRES_PASSWORD`, `POSTGRES_CONNECTION_STRING` / `DATABASE_URL`)
> is optional and configured via `/opt/daleel/.env`; the bundled `postgres` service in
> [`deploy/docker-compose.yml`](deploy/docker-compose.yml) provides it. Unset ⇒ the app runs
> SQLite-only and the `/admin/usage` dashboard shows a "not configured" hint.
