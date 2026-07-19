# Store inventory monitor: keep a monitored store's whole catalogue fresh

**Goal.** Mark a store as *monitored* and Daleel keeps its ENTIRE inventory synced — every item,
price, availability — refreshed on a cadence, feeding the same entity index the search pipeline
uses. Dedup (specs 2026-07-19) is the prerequisite that makes this safe: full-catalogue ingestion
without identity convergence would mint thousands of duplicates; with it, every listing lands as
an offer under its one item.

## What already exists (reuse, don't rebuild)

- **`StoreCrawlWorkflow`** — the LLM store crawler already walks site search / category pages /
  Shopify `/products.json`, paginated, extracting price/stock/SKU. It is query-scoped today.
- **CF edge catalogue crawl** (`ExtractCatalogAsync` + scrape-worker queue drain) — cheap bulk
  fetch; `ScrapedPrice` rows are already the per-store price series (with `ImageUrl`).
- **`EnrichmentWorkItems`** — the durable queue: per-unit lease/retry/dead ledger. The sync fans
  out through it; no watchdogs, ever.
- **`SiteSearchProfile`** — learned per-domain interface (search template). Extend, don't fork.
- **Save-path identity convergence** — inventory upserts flow through `SearchEntityStore.SaveAsync`
  and converge onto existing items; an offer update updates the one item.

## Design

### 1. Data
- `Store`: `MonitorEnabled` (bool), `MonitorCadenceHours` (int, default 24), `LastInventorySyncAt`,
  `LastInventoryCount`.
- `SiteSearchProfile` gains the learned CATALOGUE interface: `CatalogKind`
  ("shopify-json" | "sitemap" | "category-walk"), `CatalogEntryUrl`, and a per-URL
  **content-hash table** (`StoreCatalogPage`: Domain, Url, ContentHash, LastSeenAt) so unchanged
  pages skip LLM extraction entirely — the recurring cost of a sync is proportional to what
  CHANGED, not to catalogue size.
- Presence tracking rides on `ScrapedPrice.LastSeenAt` (new column): an item of a monitored store
  not seen for N consecutive syncs flips its offer `Availability` to unavailable — never deleted
  (a delisted product's page/history stays).

### 2. Sync pipeline (queue-native)
A scheduler hosted service (same shape as `EntityDedupService`) enqueues one
**`inventory.sync`** unit per due monitored store. That unit:
1. Resolves the catalogue interface (profile → probe: `/products.json` → sitemap → category tree),
   persists what it learned.
2. Fans out **`inventory.page`** units (one per catalogue page / JSON page) through the queue —
   UNCAPPED per the no-result-caps invariant; pagination loop-safety ceiling only, and an optional
   `PIPELINE_MAX_*` operator restraint.
3. Each page unit: fetch via the FULL provider chain (edge worker preferred, CF Browser fallback),
   hash-skip if unchanged, else markdown-first harvest → LLM extraction only for unstructured
   pages (Shopify JSON needs none) → upsert entities via `SearchEntityStore` (identity-keyed) +
   `ScrapedPrice` observations → enqueue ImageCheck for new photos (fail-closed rule).
4. A settle-gated **`inventory.finalize`** unit (OpenCount gate, like Synthesize) computes the
   delta — new items, price moves, missing items → availability flips — stamps
   `LastInventorySyncAt/Count`, and emits one SystemEvent for the timeline.

### 3. Scheduling & cost
- Cadence per store (`MonitorCadenceHours`); scheduler enqueues only when due — restarts are
  harmless (the queue is idempotent per sync id; `EnqueueFanOutAsync` guards duplicate fan-out).
- Cost shape: Shopify stores ≈ free (JSON, no LLM). HTML stores pay LLM only on changed pages
  (content hash). All calls metered under existing call sites + a `pricing.*` row for sync fetches.
- Vendor courtesy: per-domain fetch width 1–2, spacing between pages — a monitored store must
  never feel crawled.

### 4. Surfaces
- `/admin/stores`: Monitor toggle + cadence + last-sync + inventory count columns.
- `/store/{id}`: "Monitored" badge, inventory count, last refresh; its items list = live entity
  query filtered by an offer from this store (`/items?store=` filter reuses the directory).
- Search benefit: monitored-store items are ALREADY in the index with fresh prices — the grid's
  brand/store attach and the cache path pick them up with zero extra spend at search time.

### 5. Order of work
1. Schema (Store monitor fields, `StoreCatalogPage`, `ScrapedPrice.LastSeenAt`) + migration.
2. Catalogue-interface probe + Shopify JSON fast-path (pure fetch, no LLM) → `inventory.sync`/
   `inventory.page`/`inventory.finalize` units + scheduler.
3. HTML stores: reuse `StoreCrawlWorkflow`'s assess/paginate prompts in inventory mode
   (no query — walk everything), hash-skip.
4. Presence/availability flips + delta SystemEvent.
5. Admin toggle + store-page badge + `/items` store filter.
6. QA: monitor one Shopify store (e.g. a known .jo Shopify) + one HTML store; verify counts,
   a price change, and a delisting flip across two cadences.
