# daleel-filter-worker

The **halal-signal execution host** from `docs/architecture/cloudflare-workers-pipeline.md` (§3.4):

> **Replaces:** the LLM and vision *layers* of `HalalModerator` (`LlmHalalClassifier`,
> `OpenRouterImageHalalClassifier`). **Keep the whitelist and keyword layers, and all policy/veto
> logic, on the VPS** — they read admin-tuned Postgres tables (`ModerationWhitelistEntry`,
> `ModerationRuleOverride`) and encode the invariants that must not drift (fail-open, riba never
> filtered, show-by-default ≥ 0.8, haram-wins dedupe, image-strip ≠ removal, Arabic word-boundary
> matching). The worker is a *stateless classifier the VPS calls*, not the moderation authority.

## FINDINGS ONLY — the worker never decides

This worker returns **raw classification signals**. The VPS `HalalModerator` keeps **all** policy.
The invariants, verbatim (these live on the VPS and must not drift into this worker):

- **fail-open** — a worker error (or timeout, or unparseable model output) means **"no finding"**;
  the VPS shows the content and never blocks the pipeline on this worker
- **riba never filtered** — a store's financing model is not haram content; the user can pay cash.
  Interest-based finance is **not a category** here: the prompts forbid it *and* the worker's
  category allow-list cannot express it (mirrors `HalalPolicy.NeverFiltered`), so a riba finding
  can never leave this worker regardless of what a model says
- **show-by-default ≥ 0.8** — removal thresholds are applied on the VPS, never here
- **haram-wins dedupe** — VPS
- **image-strip ≠ removal** — VPS
- **Arabic word-boundary matching** (keyword layer) — VPS

## A/B before any default flip

Per doc §6 Phase 3, this worker's output feeds an **A/B against the current OpenRouter classifier
on a labeled set BEFORE any default flips** — moderation precision is a tracked metric. Until that
A/B passes, the worker runs in shadow: its findings are logged and compared, not acted on.

## Endpoints

| Method | Path | Mode | Body |
|---|---|---|---|
| GET | `/health` | — | — |
| POST | `/filter/text` | sync | `{ items: [{id, text, sourceUrl?}] }` (max 50 items) |
| POST | `/filter/images` | sync | `{ urls: [string] }` (max 20; http(s) only, ≤ 4 MiB each) |

Auth: `Authorization: Bearer <AUTH_TOKEN>` (or Basic with the token as password). Same house
pattern as `workers/scrape-worker` — fail-closed, constant-time compare.

### `/filter/text` → two best-effort signals per item

| Signal | Model | `source` | Notes |
|---|---|---|---|
| Safety classifier | `@cf/meta/llama-guard-3-8b` | `llama-guard` | Categories are **generic safety** (S1–S14: hate, sexual_content, …), *not* halal-specific — corroboration only. Binary verdict, so confidence is a fixed nominal `0.5` (below every VPS removal threshold by design). |
| Halal screening | `@cf/meta/llama-3.1-8b-instruct` | `llm` | The halal-specific prompt (categories mirror `PromptTemplates.HalalGuard`: alcohol, pork, gambling, adult, immodest, drugs, tobacco), JSON mode, batched 10 items/call. |

Response: `{ ok, result: { findings: [{id, category, confidence, reason, source}] }, meta }` —
items with no finding are simply **absent**. `meta.signalFailures` counts items whose signal call
errored (those items got no finding — fail-open), so the A/B harness can distinguish "clean" from
"signal never ran" in aggregate.

### `/filter/images` → vision signal per URL

`@cf/meta/llama-3.2-11b-vision-instruct` with the same halal categories and the same riba
exclusion; `source: "vision"`. Fetch guard: http(s) URLs only, streamed with a hard 4 MiB cap;
anything unfetchable/oversized/unparseable yields no finding.
Response: `{ ok, result: { findings: [{url, category, confidence, reason, source}] }, meta }`.

An optional `policy` field on either request (the doc's `HalalPolicyDto`) is accepted for
forward-compat but **ignored** — thresholds are the VPS's job.

## Provisioning (one-time, per environment)

The only Cloudflare resource is the account-level Workers AI binding (`[ai]` in `wrangler.toml`) —
nothing to create. Secrets: `AUTH_TOKEN` only.

```sh
# prod
wrangler secret put AUTH_TOKEN
wrangler deploy

# qa (worker name daleel-filter-worker-qa)
wrangler secret put AUTH_TOKEN --env qa
wrangler deploy --env qa
```

Optional model overrides (A/B iteration without a code change) go in `[vars]`:
`TEXT_GUARD_MODEL`, `TEXT_LLM_MODEL`, `VISION_MODEL` — defaults live in `src/index.js`.

## How the VPS reaches it

Rendered into `.env` by the deploy workflows (both endpoints are sync — no queues, no polling):

| Var | Meaning |
|---|---|
| `CF_FILTER_WORKER_URL` | this worker's base URL (prod: `daleel-filter-worker`, QA: `daleel-filter-worker-qa`) |
| `CF_FILTER_WORKER_TOKEN` | the `AUTH_TOKEN` secret above |

Runtime enablement is a separate admin flag (`cloudflare.execution.enabled` in `/admin/settings`),
so the inline OpenRouter path remains the instant fallback (strangler-fig, doc §6) — and stays the
authority until the A/B above passes.

## Local dev

```sh
cp .dev.vars.example .dev.vars   # fill in the token
wrangler dev                     # env.AI proxies to the real Workers AI API (billed)
```
