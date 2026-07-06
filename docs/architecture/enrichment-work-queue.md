# Enrichment work queue

Post-result enrichment (specs, images, live prices, conditions) used to run as ONE detached
in-process pass under a 600-second watchdog: all-or-nothing, silently abandoned on timeout, and —
once the Cloudflare execution layer existed — double-crawling store domains the edge was already
crawling. That shape is gone. The rule now:

> The workflow calls an API, the result is saved, the next step is called. Complex steps queue
> messages; a consumer picks each result and dives into it; dives that produce more work queue
> more messages. Every call is on its own.

## Shape

- **`EnrichmentWorkItems` (Postgres) is the queue.** One row = one unit = one API dive for one
  result piece. Claims use `FOR UPDATE SKIP LOCKED` (the same pattern as `SearchJobs`); leases
  (`LeaseUntil`) make crash recovery automatic — a dead container's claims simply expire back to
  pending. No boot reconciler, no in-memory state.
- **The search worker enqueues ONE root unit** after the base result is delivered: `enrich.plan`
  for fresh runs, `enrich.regaps` for below-threshold cache hits. That is the entire "enrich" step.
- **`enrich.plan` fans out**: a cheap brand-DB fill inline, then one `enrich.item` per model
  (spec dive), one `enrich.catalog` per store domain, one `enrich.brandresearch` per surfaced brand
  (full site/intelligence/social/catalogue research, saved to the Brand row step by step and
  freshness-gated at 7 days so no two searches repeat it), a job-level `enrich.vision` pass, and
  deliberately-late `enrich.images` / `enrich.conditions`.
- **Every unit saves immediately** through `EnrichedResultStore.PatchAsync`: a row lock on the job
  serializes concurrent patches, each mutation composes on the previous one, and the conversation,
  history row, result cache and live UI (`Enriched` broadcast) sync after each commit. A deploy
  mid-enrichment loses nothing — finished units are already persisted, unfinished ones re-lease.
- **Failure is per-unit**: each handler has its own wall-clock budget; expiry retries THAT unit
  with backoff (`NotBefore`), and exhausted retries land in a visible `dead` status with the
  reason. There is no phase- or job-level enrichment timeout anywhere, by design.
- **Follow-ups go through the queue**: `enrich.images` spends a bounded paid batch per execution
  and enqueues its own continuation when imageless items remain — unbounded coverage, small
  retryable attempts.

## Edge-aware cataloguing (no double crawls)

`enrich.catalog` matches ALREADY-DRAINED edge prices first: the base search submits store crawls
to the scrape-worker, the poll drain persists `ScrapedPrices` rows, and the unit attaches those
rows for free. When the edge is enabled and nothing has landed yet, the unit *retries later*
(the queue mediates the race with the drain — attempts 1–3 wait ~45s each); only then does it
fall back to an inline Context.dev crawl. With the edge off, it crawls inline on attempt 1.

## Metering + caps

The consumer wraps every execution in its own `JobApiCallCollector` + `AmbientApiObserver`, so
unit spend lands in the same per-job `ApiCallLogs` ledger and event firehose as the base run.
The per-job cost cap is enforced CUMULATIVELY: before a unit runs, the job's spend to date is
subtracted from `cost.max_per_job`; nothing left ⇒ the unit dies visibly ("cost cap") — a queued
deep-dive can never grant a job a fresh budget.

## Knobs

- `PIPELINE_ENRICH_CONCURRENCY` — units executed concurrently per instance (default 6). A
  throughput width, not a cap: units queue for a slot, nothing is dropped.
- Retry budgets are per-kind (`MaxAttempts` on the row): 4 by default, 6 for catalog units
  (drain-waiting), 2 for the cache gap refill.
- The old `Enrichment:TimeoutSeconds` knob is gone with the watchdog it configured.
