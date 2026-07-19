# Entity dedup: identity hashing at save + a dedup worker

**Problem.** The `/items` directory (and the entity index generally) carries many duplicates of the
same real-world product. `EntityRecord.Id` comes from `StableId.ForEntity` (`StableId.cs`), which
hashes **brand+model** — or the raw **name** when both are blank. So the same product saved twice
under differing extractions gets two rows:

1. **Name-basis drift** — no brand/model extracted, and the listing name varies per store
   ("Gree AC 12000" vs "AC Gree 12 000 BTU" vs the Arabic name) → different hashes.
2. **Basis flip** — one run extracts the brand, another doesn't → brand+model basis vs name basis.
3. **Model-string variants** — "TAC-24CHSD/TPH11I" vs "TAC-24CHSD" vs a marketing name for the
   same SKU.
4. **Cross-language** — Arabic vs English names of one product (zero shared tokens; the item-yield
   chain's trust levels already accept these cross-language, which *creates* index dups).

`UpsertAsync` dedupes only on identical `Id` — none of the above collide, so all persist.

---

## Part 1 — identity hashing at save time (stop new duplicates)

Three-tier key, strongest available wins. Computed in `StableId` (shared, unit-testable) and stored
on `EntityRecord` as a new indexed `IdentityKey` column; `SearchEntityStore.SaveAsync` resolves an
existing row by `IdentityKey` **before** falling back to insert, and merges into it (the existing
`ApplyUpdates` additive-fill rules).

- **Tier 1 — canonical SKU key** (`sku:{brandKey}:{modelKey}`): when a model/SKU is present.
  `modelKey` = `NormalizeIdentity` + strip separators (`-`, `/`, spaces) inside token runs so
  "TAC-24CHSD/TPH11I", "TAC 24CHSD TPH11I" and "tac24chsd tph11i" all collide. This aligns with the
  learning-search decision "SKU-when-present".
- **Tier 2 — brand+model key**: today's `ProductProfile.KeyFor` basis, unchanged (backward
  compatible — existing `ProductKey` rows keep their identity).
- **Tier 3 — name fingerprint** (`fp:{geo}:{tokens}`): normalized name → drop marketing/stopword
  tokens (a small static list: "original", "new", "offer", "أصلي", "جديد", "عرض"…) → **sort the
  remaining tokens** (order-insensitive) → join. Catches reordering and filler drift. Scoped by
  `Geo` — a name-only identity is only trustworthy within one market.

What tier 3 deliberately does NOT catch: cross-language dups and model-string vs marketing-name
dups. Those need judgment, not hashing — that's the worker's fuzzy tier (Part 2). Don't force them
into the hash: a too-aggressive fingerprint merges *different* products, and a wrong merge is worse
than a duplicate (same principle as "wrong items are worse than a short grid").

**Not changed:** `Id` (the routing hash in URLs) stays as-is — `/product/{id}` links must keep
working. `IdentityKey` is a *save-time convergence key*, not a routing key.

**Migration:** add `IdentityKey` (nullable, indexed) + backfill existing rows in C# on startup
(compute from Name/Brand-via-BrandId/Geo; same one-shot pattern as other seed passes). Rows whose
backfilled key collides are NOT auto-merged by the migration — they become the worker's first
work-list.

## Part 2 — the dedup worker (clean up existing duplicates)

A maintenance pass, not an enrichment unit — it runs globally, on a schedule + admin trigger, and
must never touch a live search. New hosted service `EntityDedupService` mirroring
`ProfileRefreshService`'s shape (interval loop, off by default via SystemConfig flag
`dedup.enabled`, `dedup.interval_hours`), plus a "Run now" + report surface on `/admin/data`.

**Stage A — candidate generation (SQL, cheap).** Buckets of >1 row grouped by, in order:
`IdentityKey`, `ProductKey`, `NameKey+Geo`. Exact-key buckets are *confirmed* duplicates.

**Stage B — fuzzy candidates (bounded, metered).** Within the same `Geo` (+ same `BrandId` when
both have one): token-set Jaccard over normalized names above a threshold, and cross-language pairs
sharing `BrandId` + a model-ish token (digits/latin runs survive Arabic text). Each candidate pair
is judged on the STRONGEST evidence available, in order:

1. **Vision compare** — when both rows have a product photo, the existing `IVisionMatcher`
   ("same physical product?") decides; verdicts are memoized in `VisionMatchCacheRepository`, so
   repeat passes are free. This is deliberately the PRIMARY fuzzy signal: it needs no SKU, no
   shared language, and no brand catalogue. (Catalogue-based identification is currently
   unreliable for items — do not build the dedup path on it; when it works it upgrades a row to
   tier 1/2 *before* stage B, which is a bonus, not a dependency.)
2. **Spec/text judgment** — photo missing on either side: one batched LLM call over names + specs
   (tonnage/BTU equivalence, capacity, color). New `dedup` call site in `LlmCallSites` →
   admin-switchable model, metered like every other call.

Fail-open: unavailable vision/LLM defers fuzzy pairs to the next run; exact-key merges proceed
regardless. A pair with no photo overlap AND no distinguishing specs stays unmerged (wrong merge >
duplicate).

**Stage C — merge (the only writer).** Per bucket, inside one transaction:
1. **Survivor**: the row with a `BrandId`+`ProductKey`, else richest R2 doc (offers+images count),
   else newest `LastRefreshed`.
2. **R2 docs merge additively** into the survivor's document (offers union by Source+Url, images
   union, specs fill-blanks-only, pros/cons union) — rewritten under the survivor's key. Loser docs
   are left in R2 (cheap, auditable) but their index rows are re-pointed.
3. **Losers become alias rows**, not deletions: keep the row, null the payload pointers, add
   `MergedIntoId` (new nullable column). `/product/{id}` resolves an alias by following
   `MergedIntoId` — old shared links keep working. `/items` and every directory query filter
   `MergedIntoId IS NULL`.
4. **Ledger**: one `EntityMergeLog` row per merge (survivor, losers, tier, judged-by) — visible on
   `/admin/data`, and the undo path (aliases retain their old `R2Key`).

**Safety rails.**
- Dry-run first: the report lists proposed merges with names side-by-side before `dedup.enabled`
  ever flips on; admin can veto pairs (same veto pattern as the moderation auto-reviewer).
- Never merge across `Geo`. Never merge two rows that both carry *different* `ProductKey`s with
  different `BrandId`s (conflicting strong identities = not a dup, whatever the names say).
- Batched (e.g. 50 buckets/run), idempotent (aliases are skipped by candidate generation), and
  every LLM call metered under the `dedup` call site.

## Order of work

1. `StableId` tiered `IdentityKey` + tests (variants that must collide / must not collide).
2. Migration (`IdentityKey`, `MergedIntoId`, `EntityMergeLog`) + startup backfill.
3. Save-path convergence in `SearchEntityStore.SaveAsync` (stops the bleeding).
4. Alias-aware reads (`/product/{id}` redirect, `MergedIntoId IS NULL` filters).
5. Worker Stage A + C (exact keys only) + `/admin/data` report, dry-run default.
6. Stage B fuzzy tier + LLM judgment + veto UI.
