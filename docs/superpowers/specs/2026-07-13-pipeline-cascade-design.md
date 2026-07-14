# Pipeline cascade: decompose discover → scrape → extract → enrich into queue steps

**Date:** 2026-07-13
**Owner directive:** "discover should queue scraping, which should queue deeper scraping, which should
queue brand and product enrichment — each a step in the workflow, not grouped." Restates the
2026-07-08 learning-search / scale-to-hundreds initiative (see memory `learning-search-initiative`).

## Problem (measured 2026-07-08)

Scale caps at ~8 products regardless of breadth knobs. Two architectural causes:

1. **One monolithic grouped extraction.** The base run's `ExtractProductsActivity` →
   `AgentService.ExtractProductListingsAsync` makes ONE giant LLM call over the whole gathered
   context. Uncapping it (to get more items) makes it hang past the 10-min deadline; capping it
   holds the grid at ~8. It cannot be both uncapped and fed a lot.
2. **Brand catalogues enrich but don't populate.** `ItemEnrichmentService` merges a Context.dev
   brand catalogue's specs/image ONTO items extraction already found; a brand's catalogue products
   never become NEW grid models. Discovering 5 brands adds 0 items.

Discovery is also grouped: `GatherSourcesActivity` fans web/shopping/places/social in one
synchronous step, so scraping and extraction can't be independently retried, observed, or scaled.

## Design: a cascade of discrete queue units

Extends the existing durable `EnrichmentWorkItem` queue (PR #40 / `enrichment-work-queue.md`). Each
hop is its own unit — independently retryable, observable (per-search log scope + timeline), and
scalable — enqueuing the next hop:

```
discover        per source (web / places / brand-list) → enqueue one scrape unit per URL/store/brand
scrape          fetch ONE store search page / brand catalogue page → enqueue deep-scrape + extract
deep-scrape     follow category/product links from that page → enqueue extract per page
extract         ONE small/fast LLM (or structured Context.dev) call over ONE source → CREATE grid models
brand-enrich    per brand: full Context.dev catalogue → each CatalogProduct becomes a grid model
product-enrich  per item: specs / price / image / condition (today's ItemDive/PriceFetch/ImageLookup)
synthesize      settle-gated, unchanged
```

**Fast seed preserved.** The base run keeps a minimal synchronous discover+extract (Places + one
query) so the UI paints in ~60s; the cascade streams depth in afterward. No blank-screen regression.

## Invariants (carried from the existing queue)

- **No monolithic LLM call** — extraction is per-source, small, fast, unioned.
- **Per-unit retry / dead-letter ledger**; no phase- or job-level enrichment timeout.
- **Scoped DbContext is single-threaded** — never touched inside a concurrent fan-out (load before,
  write after). This bit prod once ("command already in progress").
- **Best-effort** — a unit that yields nothing never faults the search.
- **Settle-gated synthesis** waits on `OpenCount` for the whole cascade to drain.
- **Idempotent** — re-running a unit re-bills nothing (HWM / dedup by identity key).
- **Per-search trace** — every unit runs under `SearchLogScope` (SearchJobId + UnitKind).

## Staged delivery (highest leverage first, each TDD'd + QA-verified)

1. **Brand catalogue → grid source + per-source chunked extraction** (the "hundreds" lever). New
   `enrich.brandcatalog` unit turns each Context.dev `CatalogProduct` into a grid `ProductModel`
   (structured, no LLM); split the monolithic extraction into per-source units (`enrich.extract`)
   each doing one small LLM call, unioned via the existing additive merge.
2. **Decompose `CatalogAttach`** into `enrich.scrape` → `enrich.deepscrape` → `enrich.extract` —
   one page per unit, follow-links enqueue the next.
3. **Move discovery into the queue** — `enrich.discover` per source enqueues the scrape units; the
   base run drops to the minimal seed.

Each stage ships and is verified on QA before the next.

## Non-goals

- No change to the relevance/halal filters, the cache design, or SKU (separate initiative phases).
- No new external providers.
