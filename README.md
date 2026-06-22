# Daleel (دليل)

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
| `Daleel.Agent`    | The intelligence engine: LLM clients (OpenAI/Anthropic), prompt templates, `AgentService` (planner + analyst). |
| `Daleel.Cli`      | Console entry point. |

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
dotnet build          # build the solution
dotnet test           # run all 159 tests (xUnit + FluentAssertions)
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

---

## API keys & setup

Daleel reads credentials from environment variables. Set only the ones for the capabilities
you want — the agent wires up whatever is available and skips the rest.

| Variable | Used for | Required for |
|----------|----------|--------------|
| `ANTHROPIC_API_KEY` | Anthropic Claude (preferred LLM) | any agent command |
| `OPENAI_API_KEY` | OpenAI (LLM fallback) | any agent command (if no Anthropic key) |
| `SERPAPI_KEY` | SerpAPI — Google Web/Shopping/Maps in one | web/shopping/maps search |
| `BING_SEARCH_KEY` | Bing Web Search (search fallback) | web/news search (if no SerpAPI) |
| `GOOGLE_PLACES_API_KEY` | Google Places API (New) | `stores`, `nearby`, store enrichment |
| `CONTEXT_DEV_API_KEY` | Context.dev — scrape→markdown, brand data, AI extract | deep-reading pages, brand enrichment |
| `CLOUDFLARE_ACCOUNT_ID` + `CLOUDFLARE_API_TOKEN` | Cloudflare Browser Rendering (scrape fallback) | rendering JS-heavy / anti-bot pages |
| `APIFY_TOKEN` | Apify actors | social monitoring (`search`, `monitor`), social fetch in agent |

```bash
export ANTHROPIC_API_KEY=sk-ant-...
export SERPAPI_KEY=...
export GOOGLE_PLACES_API_KEY=...
export CONTEXT_DEV_API_KEY=...
export CLOUDFLARE_ACCOUNT_ID=...   CLOUDFLARE_API_TOKEN=...
export APIFY_TOKEN=apify_...
```

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
│   └── Daleel.Cli/             # console entry point
├── tests/                      # Core / Pipeline / Search / Agent test suites (159 tests)
└── docs/
    └── architecture.md
```
