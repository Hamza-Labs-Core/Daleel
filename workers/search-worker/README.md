# daleel-search-worker

The **search execution host** from `docs/architecture/cloudflare-workers-pipeline.md` (§3.5):

> A thin fan-out over external search/shopping/places APIs, fronted by a **KV cache** keyed on the
> normalized query so hot repeats short-circuit at the edge. […] **Postgres `SearchCache` stays the
> source of truth**, KV is a best-effort accelerator.

This is the **Phase A** cut: the existing vendor calls relocated **verbatim** as a caching proxy —
not a reimplementation. The VPS's `SerpApiProvider` / `GooglePlacesProvider` keep all of their own
engine mapping and response parsing and simply point their base URL at this worker, which:

- **holds the vendor keys** (`SERPAPI_KEY`, `GOOGLE_PLACES_API_KEY`) as worker secrets, off the
  VPS, and injects them per request (`api_key` query param / `X-Goog-Api-Key` header);
- **caches hot repeats in KV** under canonical keys, returning the raw vendor JSON byte-for-byte
  with the vendor's status code and an `X-Cache: hit|miss` header;
- **never caches non-200s**, and treats any KV failure as a plain miss — the cache can accelerate
  a request but can never fail one. Postgres `SearchCache` on the VPS stays authoritative.

## Endpoints

| Method | Path | Forwarded to | Cache key | TTL |
|---|---|---|---|---|
| GET | `/health` | — | — | — |
| GET | `/serpapi/search?<params>` (alias: `/search.json`) | `https://serpapi.com/search.json` + `api_key` | `serp:v1:` + SHA-256 of the name-sorted query string (api_key stripped first) | `CACHE_TTL_SECONDS` (default 3600, min 60) |
| GET/POST | `/places/{path}` (alias: `/v1/{path}`) | `https://places.googleapis.com/{path}` + `X-Goog-Api-Key` | `places:v1:` + SHA-256 of path + body + field mask | 900 s fixed |

- The **provider-native aliases** (`/search.json`, `/v1/*`) are what the untouched VPS
  `SerpApiProvider`/`GooglePlacesProvider` actually request — pointing their base URL here is the
  entire integration.
- A caller-sent `api_key` is **stripped and replaced** with the worker's secret (the VPS provider
  always attaches one — its real key, or the `edge-proxied` placeholder when the key lives only
  here). It never reaches the vendor and never affects the cache key.
- `POST /places/*` forwards the remaining path, query string, and JSON body unchanged, and passes
  the caller's `X-Goog-FieldMask` header through (the field mask shapes the response, so it is
  part of the cache identity).
- If a vendor secret is unset the relevant endpoint fails **closed** with `500
  server_misconfigured` — never an unauthenticated pass-through.

Auth: `Authorization: Bearer <AUTH_TOKEN>` (or Basic with the token as password). Same house
pattern as `workers/scrape-worker` — fail-closed, constant-time compare. Proxied responses are the
**raw vendor body** (no `{ok,...}` envelope) so the VPS providers parse them exactly as before;
worker-generated errors (auth, 400s, 404s, 500s) use the standard envelope.

## Provisioning (one-time, per environment)

**CI does this automatically**: each environment's deploy workflow (deploy-workers.yml prod / deploy-workers-qa.yml QA) runs
`workers/provision.sh search-worker <env>` before its deploy — it resolves each KV namespace by title (creating it when missing)
and injects the id into `wrangler.jsonc`'s placeholder. The commands below are the manual
equivalent (paste the id yourself, or just run the script):

```sh
cd workers/search-worker

# prod
wrangler kv namespace create daleel-search-cache
#   -> paste the returned id into wrangler.jsonc: [[kv_namespaces]] id = "…"
wrangler secret put AUTH_TOKEN
wrangler secret put SERPAPI_KEY
wrangler secret put GOOGLE_PLACES_API_KEY
wrangler deploy

# qa (mirrors, isolated)
wrangler kv namespace create daleel-qa-search-cache
#   -> paste the returned id into wrangler.jsonc: [[env.qa.kv_namespaces]] id = "…"
wrangler secret put AUTH_TOKEN --env qa
wrangler secret put SERPAPI_KEY --env qa
wrangler secret put GOOGLE_PLACES_API_KEY --env qa
wrangler deploy --env qa
```

## How the VPS reaches it

`SerpApiProvider` / `GooglePlacesProvider` get a base-URL override pointing at this worker
(rendered into `.env` by the deploy workflows):

| Var | Meaning |
|---|---|
| `CF_SEARCH_WORKER_URL` | this worker's base URL (providers request `{url}/search.json` and `{url}/v1/...` — the native aliases) |
| `CF_SEARCH_WORKER_TOKEN` | the `AUTH_TOKEN` secret above, sent as `Authorization: Bearer` |

With the override set, the worker's secrets are the only keys that reach the vendors — the VPS's
`SERPAPI_KEY` / `GOOGLE_PLACES_API_KEY` can be dropped from `.env` (the providers then run on the
`edge-proxied` placeholder). When unset, the providers hit the vendors directly as today
(strangler-fig fallback, doc §6).

KV here is an **edge accelerator only**: the VPS's Postgres `SearchCache` remains the
authoritative cache with its own freshness rules (doc §3.5). A KV hit just means a repeat query
skipped a paid vendor round-trip.

## Local dev

```sh
cp .dev.vars.example .dev.vars   # fill in tokens
wrangler dev
```

Check cache behavior with the `X-Cache` response header: first call `miss`, repeat within TTL
`hit`.
