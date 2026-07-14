# Search object & result-grid design

**Date:** 2026-07-14
**Status:** Approved design — ready for implementation planning
**Scope:** One combined spec covering four connected pieces: (1) a structured
search object, (2) goal-driven default sort, (3) product-type facet filters,
(4) reviews surfaced on each result card.

---

## Problem

A user's query is turned into a `SearchStrategy` (a *research plan* — query
lists plus a coarse `QueryType`/`Intent`/`Subject`). That object never captures
the query's actual structure — the product, the stated constraints (size,
colour), the location, or the user's goal — so nothing downstream can use them:

- The result grid's filters are **generic and fixed** for every product type
  (Brand, Source, Condition, price range). A TV and a pack of diapers get the
  same filter set.
- Sort is a fixed list (relevance default / price↑ / price↓ / most-sellers).
  There is **no goal-driven default** and **no rating sort**, so a "cheapest X"
  query still opens sorted by relevance.
- Social / brand-site / store **reviews are collected but never surfaced** on
  the results — only an aggregate article list renders.

Most of the *data* already exists (`ProductModel.Specs`, `Reviews`,
`RatedReviews`, `SocialProof`+`Sentiment`, `BrandReputation`, `StoreReview`).
The gaps are: the query→structure step never fills a rich object, and the grid
never uses product-type/goal to shape filters/sort or renders the review data.

## Goal

Make the parsed query a first-class **search object** that carries all the
metadata needed to shape the results, and have the grid consume it: per-type
filters, goal-driven default sort, and per-card reviews. Fully additive — a
search that lacks the new metadata renders exactly as it does today.

## Non-goals

- No new review *collection* — this spec surfaces already-collected review
  data. Coverage gaps are logged, not closed here.
- No user-facing UI for the search object itself (decision: internal-only). Its
  *effects* are visible (filters/sort/reviews); the object is not shown or
  editable.
- No goal→ranking blend/weighting model — goal is a free-text string that yields
  a single default-sort recommendation, nothing more.

---

## Key decisions (from brainstorming)

| Decision | Choice |
|---|---|
| Scope | One combined spec, all four pieces, implemented in stages |
| Where the metadata lives | **Fold into `SearchStrategy`** — it becomes THE search object, not a separate type |
| Goal | **Open free-text string**; the planner also emits a concrete `DefaultSort` recommendation |
| Filters | **Hybrid** — planner names the facet *dimensions*; option *values* are data-driven from result `Specs` |
| Reviews | **On each product card** — compact signal (rating + count + sentiment + source icons) expanding to the reviews |
| Search-object UX | **Internal only** — persisted and drives the grid; no UI for the object itself |

---

## Design

### 1. `SearchStrategy` becomes the search object

`SearchStrategy` (`src/Daleel.Core/Models/QueryPlan.cs`) gains the structured
query metadata alongside its existing classification + execution-plan fields:

```
SearchStrategy {
  // ── classification (existing) ──
  QueryType, Intent, Subject, Reasoning

  // ── execution plan (existing) ──
  WebQueries, ShoppingQueries, SocialQueries, PlacesQueries, UrlsToRead

  // ── NEW: structured query metadata ──
  Product:     string                       // the actual product ("diapers")
  Specs:       IReadOnlyDictionary<string,string>  // stated constraints ("size"→"4")
  Location:    string                        // market/place ("Amman")
  Goal:        string                        // free text ("cheapest", "best for newborns")
  Facets:      IReadOnlyList<SearchFacet>    // filter dimensions for this product type
  DefaultSort: string                        // goal-driven default-sort recommendation
}

SearchFacet {
  Key:   string     // binds to a ProductModel.Specs key (e.g. "Screen Size")
  Label: string     // localized display label
  Unit:  string?    // optional unit hint ("inch", "L")
}
```

All new fields are **optional** with empty defaults. `Specs` (on the strategy)
are the *query's stated constraints* and are distinct from each result's
`ProductModel.Specs` (the per-card extracted attributes); facets bind the two.

**Produced** in `AgentService.PlanAsync` (`src/Daleel.Agent/AgentService.cs`):
the planning prompt and its response DTO (`ToStrategy()`) are extended to emit
the new fields. Best-effort — if the planner omits them, they stay empty and the
grid falls back to today's behavior. Never faults the search.

### 2. Persistence & delivery to the grid

`SearchStrategy` currently lives only in `SearchPipelineState.Strategy`
(in-memory). To be "the search object persisted with the id" **and** to reach
the grid, it is attached to `ProductSearchResult`, which already serializes into
`SearchJob.ResultJson` under the job's `Id` and is the exact object the grid
reads. One write persists it with the search and delivers it to the UI — **no
new DB column**.

The `ResultJson` deserializer must tolerate missing/extra fields (it already
does; the additions are optional properties). Old cached results without a
strategy deserialize with an empty/absent one.

### 3. Goal-driven default sort

- The planner maps the free-text `Goal` to a concrete `DefaultSort` value drawn
  from the grid's known sort keys (`relevance`, `price_asc`, `price_desc`,
  `sellers`, and the new `rating`). E.g. "cheapest" → `price_asc`; "best" /
  "highest rated" → `rating`.
- Fallback chain when `DefaultSort` is empty/unrecognized: a small keyword
  heuristic over the `Goal` string (cheap/lowest→`price_asc`, best/top/rated→
  `rating`) → then `relevance`.
- `ProductListings.razor` seeds its initial `Sort` from
  `Result.Strategy.DefaultSort` instead of the hardcoded `"relevance"`, and adds
  a **`rating`** sort option (ordering by `Rating` desc, nulls last, tie-broken
  by `RatingCount`). The user can still override via the dropdown.

### 4. Product-type facet filters (hybrid)

- The grid renders one filter control per `SearchFacet` in `Result.Strategy.Facets`,
  **alongside** the existing generic filters (not replacing them).
- Options for a facet come from the **distinct `ProductModel.Specs[Facet.Key]`
  values present across the results** (data-driven). A facet the planner names
  but for which fewer than 2 distinct values exist is **hidden** (no useful
  filter).
- Selecting a facet value keeps models whose `Specs[Key]` matches. Query `Specs`
  that correspond to a facet **pre-select** that facet's value on first render.
- **Value normalization:** result specs vary (`55"` vs `55 inch`, Arabic vs
  English). Keys and values are normalized when bucketing options (trim, case-
  fold, unit-normalize where cheap). Imperfect matches fragment options rather
  than break filtering; normalization is best-effort.

### 5. Reviews on each product card

- A new compact **`ReviewSignal`** component renders per card: aggregate rating +
  review count + a sentiment chip, plus small **source icons** indicating which
  review kinds exist (social / brand-site / store). It expands to show the actual
  reviews.
- It reads data **already collected**:
  - product reviews — `ProductModel.Reviews` (`ItemReview`), `RatedReviews`
    (`ProductReview`), `Rating`, `RatingCount`;
  - social — `ProductModel.SocialProof` (`Reviews` + `Sentiment`);
  - brand — `ProductModel.BrandReputation`;
  - store — `StoreReview` on `Result.Stores`, associated to a card via its
    offers' sellers (a product's store reviews = reviews of the stores selling it).
- No data for a card → **no widget** (no empty shell).

---

## Component boundaries

| Unit | Purpose | Depends on |
|---|---|---|
| `SearchStrategy` (+`SearchFacet`) | The search object: classification + plan + query metadata | none (Core model) |
| Planner prompt/DTO (`AgentService.PlanAsync`) | Fill `Product`/`Specs`/`Location`/`Goal`/`Facets`/`DefaultSort` | LLM |
| `SortResolver` (goal→sort) | Map `Goal`/`DefaultSort` to a concrete sort key + heuristic fallback | pure |
| `FacetBuilder` | From `Strategy.Facets` + result `Specs`, produce renderable facets with normalized options; drop <2-value facets | pure |
| `ProductListings.razor` | Render facets + generic filters, seed sort from strategy, apply facet filtering + `rating` sort | `FacetBuilder`, `SortResolver` |
| `ReviewSignal.razor` | Compact per-card review signal + expand | `ProductModel`, `StoreReview` lookup |

The pure units (`SortResolver`, `FacetBuilder`, planner DTO mapping) are
unit-tested without a browser or an LLM.

## Error handling & edge cases

- Planner omits the new fields → empty metadata → grid falls back to generic
  filters + `relevance` sort. Search never regresses.
- Sparse/inconsistent specs → facet hidden (<2 distinct values); no broken
  controls.
- Empty/unparseable `Goal`/`DefaultSort` → keyword heuristic → `relevance`.
- Missing review data for a card → no `ReviewSignal`.
- Old cached `ResultJson` (no strategy) → renders as today.

## Testing

- **Unit:** planner DTO→`SearchStrategy` mapping (new fields); `SortResolver`
  (explicit `DefaultSort`, heuristic fallbacks, `relevance` final fallback);
  `FacetBuilder` (option derivation, normalization, <2-value hide rule,
  query-spec pre-selection); `rating` sort ordering (nulls last, tie-break).
- **Component:** `ReviewSignal` across present/absent/partial review data;
  `ProductListings` renders facets from a strategy and falls back cleanly when
  the strategy is null/empty.
- **Backward-compat:** a `ProductSearchResult` with no strategy renders with the
  generic filter set and relevance sort.

## Rollout

Fully additive. Ships behind no flag because it degrades to current behavior
when the metadata is absent. Stages: (1) `SearchStrategy` fields + planner +
persistence on `ProductSearchResult`; (2) `SortResolver` + goal-driven default +
`rating` sort; (3) `FacetBuilder` + grid facet filters; (4) `ReviewSignal` on
cards. Each stage is independently shippable and testable.
