# daleel-extract-worker

The **structured-extraction execution host** from `docs/architecture/cloudflare-workers-pipeline.md`
(§3.3):

> Takes raw HTML/markdown (from scrape-worker or an R2 key) and emits Daleel's product/spec JSON
> shape. **Workers AI in JSON mode**, schema-forced, model chosen by input length.

**v1 scope: synchronous single-document.** A single page extract completes in seconds, so this
phase serves it inline over one HTTPS call — no queues, no jobs. The doc's §3.3 async pieces —
`202 {jobId, resultKey}` batch submits via Queue, `EntityDocument` writes to R2 `daleel-data`,
the D1 `entity_index` edge mirror, and length-tiered models (Scout 131k ctx / chunk-and-merge) —
arrive in a later phase behind these same routes. v1 runs one model
(`@cf/meta/llama-3.3-70b-instruct-fp8-fast`, 24k-token context) and caps/trims input to fit it.

The prompt is parser-discipline: the model **extracts, never invents** — absent fields are `null`,
no products means an empty array, and output is JSON only. JSON mode
(`response_format: json_schema`) constrains decoding to the schema; if a model ever regresses to
raw text, the worker salvages the first balanced JSON object from it before giving up.

## Endpoints

| Method | Path | Mode | Body |
|---|---|---|---|
| GET | `/` | — | service banner + endpoint list |
| GET | `/health` | — | — |
| POST | `/extract/products` | sync | `{ content, market?, intent? }` |
| POST | `/extract/structured` | sync | `{ content, schema }` |

Auth: `Authorization: Bearer <AUTH_TOKEN>` (or Basic with the token as password). Same house
pattern as `workers/scrape-worker` — fail-closed, constant-time compare, OPTIONS answered pre-auth.

### `/extract/products`

`content` is page text/markdown (typically scrape-worker's `/scrape/page` output). `market` and
`intent` are optional hints (currency/locale disambiguation and relevance — never license to
invent values). Success envelope:

```json
{
  "ok": true,
  "mode": "sync",
  "model": "@cf/meta/llama-3.3-70b-instruct-fp8-fast",
  "result": {
    "products": [
      { "name": "…", "description": null, "price": 12.99, "currency": "USD",
        "url": null, "category": null, "imageUrl": null, "sku": null }
    ],
    "productCount": 1
  },
  "meta": { "ms": 2140, "truncated": false }
}
```

`result.products[]` is the exact **`CatalogProduct`** shape the VPS deserializes
(`src/Daleel.Search/Providers/ContextDevProvider.cs` — camelCase on the wire, matched
case-insensitively; `price` is `number|null`, everything except `name` nullable). Identical
contract to scrape-worker's catalogue results, so the VPS reuses one deserializer.

### `/extract/structured`

Generic schema-forced extraction: the caller supplies the JSON Schema, the same flow runs, and
the envelope carries `result: { data }` — whatever the schema described. This is the seam
`ItemDeepDiveWorkflow`'s `ExtractSpecs` migrates onto.

### Limits & errors

| Condition | Response |
|---|---|
| `content` > 100,000 chars (~100 KB) | `413` `{ok:false, error:{code:"payload_too_large", retryable:false}}` |
| `content` > 90,000 chars (model ctx ≈24k tokens) | processed, trimmed; `meta.truncated: true` |
| Workers AI call failed | `502` `{code:"ai_unavailable", retryable:true}` |
| Model output not valid/expected JSON | `502` `{code:"extraction_unparseable", retryable:true}` |
| Missing/invalid `content` or `schema` | `400` `{code:"bad_request", retryable:false}` |
| Unknown route | `404` envelope; unhandled errors → generic `500` (details in worker logs only) |

## Provisioning (one-time, per environment)

Workers AI needs **no resource creation** — the `[ai]` binding is account-level (enabled by
default on the account). The only secret is the auth token:

```sh
# prod
wrangler secret put AUTH_TOKEN
wrangler deploy

# qa (deploys as daleel-extract-worker-qa)
wrangler secret put AUTH_TOKEN --env qa
wrangler deploy --env qa
```

No KV/R2/D1/Queues in v1 (see scope note above) — when Phase 2 adds them, QA resource names
must follow the `daleel-qa-` prefix convention.

**Cost note:** Workers AI bills per neuron and **bills even under `wrangler dev`** (no local
mock). The 70B model burns neurons ~8× faster than 8B-class models, so the 10k free neurons/day
evaporate quickly — keep local testing short.

The VPS needs (rendered into `.env` by the deploy workflows, consumed through `ProviderApi`):

| Var | Meaning |
|---|---|
| `CF_EXTRACT_WORKER_URL` | this worker's base URL (`https://daleel-extract-worker[-qa].<account>.workers.dev` or the custom route) |
| `CF_EXTRACT_WORKER_TOKEN` | the `AUTH_TOKEN` secret above |

Runtime enablement stays behind the admin flag (`cloudflare.execution.enabled` in
`/admin/settings`), so the inline VPS path remains the instant fallback (strangler-fig, doc §6).

## Local dev

```sh
cp .dev.vars.example .dev.vars   # fill in tokens
wrangler dev
```

```sh
curl -s -X POST http://localhost:8787/extract/products \
  -H "Authorization: Bearer dev-token" -H "Content-Type: application/json" \
  -d '{"content":"# Widget Pro\nOur flagship widget. $49.99 USD. SKU WP-100.","market":"USA"}'
```
