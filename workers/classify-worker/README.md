# daleel-classify-worker

The **classification execution host** from `docs/architecture/cloudflare-workers-pipeline.md`
(§3.2): *"text/images → labels + confidence"* on **Cloudflare Workers AI**.

> **Replaces:** the commodity classification currently done by LLM calls (buy-intent heuristics,
> category tagging, the keyword/LLM adjudication *signal* inside moderation) — **not** the
> strategy planning, which stays on Claude/OpenRouter.

The worker is deliberately **stateless and policy-free**: it turns content into
`{label, confidence}` verdicts and nothing else. Thresholds, whitelists, dedupe, veto logic, and
every moderation invariant stay with the caller (the VPS). Mode is **sync per batch** — a request
returns its verdicts in the same response, with per-item error tolerance (one failed model call
yields an error verdict, never a failed batch).

## Endpoints

| Method | Path | Mode | Body |
|---|---|---|---|
| GET | `/health` | — | — |
| POST | `/classify/text` | sync | `{ items: [{id, text}] (max 100), labels: [string,...], model? }` |
| POST | `/classify/images` | sync | `{ urls: [string,...] (max 20), prompt?, model? }` |

Auth: `Authorization: Bearer <AUTH_TOKEN>` (or Basic with the token as password). Same house
pattern as `workers/scrape-worker` / `workers/log-viewer` — fail-closed, constant-time compare.

### `/classify/text`

Each item runs against `model` (default `@cf/meta/llama-3.2-3b-instruct`) with a strict
single-label prompt and the model's **JSON mode**
(`response_format: { type: "json_schema", json_schema }` — the schema `enum`s the caller's
labels). If the binding rejects `response_format`, the call falls back to a plain prompt and the
first JSON object is scanned out of the raw response (linear brace scan, no regex).

```jsonc
// req
{ "items": [ { "id": "l1", "text": "Refurbished — 90-day warranty" } ],
  "labels": ["new", "used", "refurbished", "unknown"] }
// res
{ "ok": true, "mode": "sync",
  "result": { "verdicts": [ { "id": "l1", "label": "refurbished", "confidence": 0.94, "reason": "explicit 'refurbished'" } ] },
  "meta": { "ms": 812, "model": "@cf/meta/llama-3.2-3b-instruct", "items": 1 } }
```

`label` is always one of the caller's labels (echoed verbatim) or `null`. A failed model call or
an off-schema answer yields `{ id, label: null, confidence: 0, reason: "error" | "model returned an off-schema label" }`.
Beyond 100 items → `413`.

### `/classify/images`

Each url is fetched (http/https only; bodies over **4 MiB** rejected — enforced while streaming)
and run through `model` (default `@cf/meta/llama-3.2-11b-vision-instruct`) with
`prompt` (default `"Describe the main subject in one short label."`). Beyond 20 urls → `413`.

Verdict shape `{ url, label, confidence }`:

- classifier models (e.g. `@cf/microsoft/resnet-50`) → top class and its **score**;
- generative vision models return no calibrated score → `label` is the trimmed response text and
  `confidence` is `1` when a label was produced (callers own thresholds);
- any per-url failure (bad scheme, fetch error, too large, model error) →
  `{ url, label: null, confidence: 0, reason }`.

## Cost profile (Workers AI)

$0.011 / 1,000 neurons; **10,000 neurons/day free** (QA can largely live free on small models).

| Model | Approx. $/M tokens (in / out) | Note |
|---|---|---|
| `@cf/meta/llama-3.2-3b-instruct` (default text) | $0.05 / $0.34 | 80k ctx — the high-volume workhorse |
| `@cf/meta/llama-3.1-8b-instruct-fp8` | $0.15 / $0.29 | 32k ctx |
| `@cf/meta/llama-3.3-70b-instruct-fp8-fast` | $0.29 / $2.25 | **burns ~8× the neurons of 8B/3B** — the free tier lasts only a handful of calls; reserve for hard cases |

- **Workers AI bills even in `wrangler dev`** — there is no local mock. Prototype on small models.
- **One AI binding per Worker** (platform limit) — this worker has exactly one (`AI`).
- `@cf/meta/llama-3.2-11b-vision-instruct` needs a **one-time license call**:
  `env.AI.run("@cf/meta/llama-3.2-11b-vision-instruct", { prompt: "agree" })` — if the first
  image classification on a fresh account fails, run that once (e.g. via `wrangler dev`).
- **Bulk classification (future path):** for non-real-time volume, the Workers AI **async Batch
  API** (`queueRequest: true`, results typically ≤5 min) gets past per-model sync rate limits.
  This worker's sync per-batch endpoints are for the current pipeline scale; when a phase needs
  thousands of labels per run, add a `202 {jobId}` batch mode here rather than fanning out sync
  calls from the VPS.

## Provisioning (one-time, per environment)

No queues, buckets, or namespaces — Workers AI is an account-level binding. Only the auth secret:

```sh
# prod
wrangler secret put AUTH_TOKEN
wrangler deploy

# qa (separate worker name + its own token; AI binding needs no per-env resource)
wrangler secret put AUTH_TOKEN --env qa
wrangler deploy --env qa
```

Optionally route the AI binding through **AI Gateway** later for caching, per-key rate limits, and
spend caps (doc §8.3) — a config change here, invisible to callers.

## How the VPS reaches it

Rendered into `.env` by the deploy workflows (same pattern as `CF_SCRAPE_WORKER_*`):

| Var | Meaning |
|---|---|
| `CF_CLASSIFY_WORKER_URL` | this worker's base URL |
| `CF_CLASSIFY_WORKER_TOKEN` | the `AUTH_TOKEN` secret above |

VPS-side call metering happens in **`ProviderApi`** (the existing per-job API-call
collector) — responses carry `meta.ms` / `meta.model` for that accounting. Runtime enablement is
the same strangler-fig admin flag family as the other workers (`cloudflare.execution.*` in
`/admin/settings`), so the inline VPS path remains the instant fallback (doc §6).

## Local dev

```sh
cp .dev.vars.example .dev.vars   # fill in tokens
wrangler dev                     # NOTE: AI calls bill for real even in dev
```
