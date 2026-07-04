# daleel-scrape-worker

The **scraping execution host** from `docs/architecture/cloudflare-workers-pipeline.md` (§3.1),
Phase 1a: the existing Context.dev calls relocated to the edge **unchanged**. Not a vendor swap —
the point is the execution shape:

- **Async & queue-backed.** A catalogue crawl is submitted (`202 {jobId, resultKey}`), processed by
  this worker's own queue consumer, and its result is durable in R2 **before** anyone is told it's
  done. A VPS-side timeout can no longer discard a finished crawl.
- **Uncapped.** No 12-product cap, no 30-second sub-workflow guillotine. The caller may pass an
  explicit `maxProducts`; by default the vendor's own ceiling applies. Cost stays bounded by the
  VPS per-job cost cap (worker calls are metered on submit) and the queue retry budget.
- **Faulted ≠ empty.** Terminal vendor failures write an `error` status doc and a poll message, so
  the VPS surfaces a real error; transient failures retry and eventually land in the DLQ.

## Endpoints

| Method | Path | Mode | Body |
|---|---|---|---|
| GET | `/health` | — | — |
| GET | `/jobs/{jobId}` | — | — |
| POST | `/scrape/page` | sync | `{ url, format? }` |
| POST | `/scrape/catalog` | async | `{ domain, maxProducts?, timeoutMs?, searchJobId?, store? }` |
| POST | `/scrape/brand` | async | `{ domain, withCatalog?, searchJobId?, store? }` |

Auth: `Authorization: Bearer <AUTH_TOKEN>` (or Basic with the token as password). Same house
pattern as `workers/log-viewer` — fail-closed, constant-time compare.

## Provisioning (one-time, per environment)

```sh
# prod
wrangler queues create daleel-scrape-work
wrangler queues create daleel-scrape-dlq
wrangler queues create daleel-poll-work
wrangler queues consumer http add daleel-poll-work        # enable VPS pull consumer
wrangler secret put AUTH_TOKEN
wrangler secret put CONTEXT_DEV_API_KEY
wrangler deploy

# qa (mirrors, isolated)
wrangler queues create daleel-qa-scrape-work
wrangler queues create daleel-qa-scrape-dlq
wrangler queues create daleel-qa-poll-work
wrangler queues consumer http add daleel-qa-poll-work
wrangler secret put AUTH_TOKEN --env qa
wrangler secret put CONTEXT_DEV_API_KEY --env qa
wrangler deploy --env qa
```

The VPS needs (rendered into `.env` by the deploy workflows):

| Var | Meaning |
|---|---|
| `CF_SCRAPE_WORKER_URL` | this worker's base URL |
| `CF_SCRAPE_WORKER_TOKEN` | the `AUTH_TOKEN` secret above |
| `CF_QUEUES_API_TOKEN` | Cloudflare API token with `queues_read` + `queues_write` |
| `CF_POLL_QUEUE_ID` | queue **ID** (not name) of `daleel[-qa]-poll-work` |
| `CLOUDFLARE_ACCOUNT_ID` | already present for R2 |

Runtime enablement is a separate admin flag (`cloudflare.execution.enabled` in `/admin/settings`),
so the inline path remains the instant fallback (strangler-fig, doc §6).

## R2 layout (bucket `daleel-data` / `daleel-qa-data`)

| Key | Contents |
|---|---|
| `{env}/jobs/{jobId}.json` | job status doc (`queued`→`running`→`done`/`error`) |
| `{env}/pipeline/{searchJobId}/catalog/{jobId}.json` | catalogue result (`products[]` in `CatalogProduct` shape) |
| `{env}/pipeline/{searchJobId}/brand/{jobId}.json` | brand profile (+ catalogue) |

## Local dev

```sh
cp .dev.vars.example .dev.vars   # fill in tokens
wrangler dev
```
