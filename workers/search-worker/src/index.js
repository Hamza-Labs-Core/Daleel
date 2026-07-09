/**
 * Daleel search-worker
 * --------------------------------------------------------------------------
 * The SEARCH execution host of docs/architecture/cloudflare-workers-pipeline.md
 * (§3.5), Phase A: the existing vendor calls relocated to the edge VERBATIM,
 * fronted by a KV cache. This is a caching PROXY, not a reimplementation —
 * the VPS's SerpApiProvider / GooglePlacesProvider keep all of their own
 * parsing/engine mapping and simply point their base URL at this worker,
 * which (a) injects the vendor API key (held as a worker secret, off the VPS)
 * and (b) short-circuits hot repeat queries at the edge.
 *
 * Endpoints (all require auth — house pattern, same as scrape-worker):
 *   GET  /                    -> service banner + endpoint list (JSON)
 *   GET  /health              -> liveness
 *   GET  /serpapi/search?...  -> proxy to https://serpapi.com/search with
 *                                api_key appended; raw vendor JSON cached in KV
 *   POST /places/{...path}    -> proxy to https://places.googleapis.com/{path},
 *                                X-Goog-Api-Key injected, X-Goog-FieldMask
 *                                passed through; response cached in KV
 *
 * Cache contract: only HTTP-200 vendor bodies are cached; responses carry
 * X-Cache: hit|miss. KV is a best-effort ACCELERATOR only — Postgres
 * SearchCache on the VPS stays the authoritative cache (doc §3.5); a KV
 * read/write failure degrades to a plain proxy call, never a request failure.
 */

const SERPAPI_BASE = "https://serpapi.com";
const PLACES_BASE = "https://places.googleapis.com";

/** Fixed TTL for Places responses (spec'd, not configurable): place data is stabler than SERPs. */
const PLACES_TTL_SECONDS = 900;

/** KV floor — expirationTtl below 60 is rejected by the platform. */
const MIN_TTL_SECONDS = 60;

/** Shadow-copy TTL (24h): how stale a SERP we'll serve when the hourly cap has tripped. */
const STALE_TTL_SECONDS = 24 * 3600;

/** Default account-wide SerpAPI budget per rolling hour; overridable via SERPAPI_HOURLY_LIMIT. */
const DEFAULT_SERPAPI_HOURLY_LIMIT = 1000;

export default {
  async fetch(request, env, ctx) {
    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: corsHeaders() });
    }

    const denied = authorize(request, env);
    if (denied) return denied;

    const url = new URL(request.url);
    let path;
    try {
      path = decodeURIComponent(url.pathname);
    } catch {
      return json({ ok: false, error: err("bad_request", "malformed percent-encoding in request path", false) }, 400);
    }

    try {
      if (request.method === "GET") {
        if (path === "/" || path === "") return root();
        if (path === "/health") return json({ ok: true });
        // "/search.json" is the provider-native alias: the VPS SerpApiProvider requests that exact
        // absolute path, so serving it means the provider needs ZERO changes to route through here.
        if (path === "/serpapi/search" || path === "/search.json") return serpApiSearch(url, env, ctx);
        // Provider-native Places alias (GET /v1/places/{id} etc.).
        if (url.pathname.startsWith("/v1/")) return placesProxy(url, request, env, ctx, "");
        return json({ ok: false, error: err("not_found", `no route ${path}`, false) }, 404);
      }

      if (request.method === "POST") {
        // Route on the RAW pathname: the remainder is forwarded to the vendor byte-for-byte,
        // so any percent-encoding the caller chose survives the hop unchanged.
        if (url.pathname.startsWith("/places/")) return placesProxy(url, request, env, ctx, "/places");
        // Provider-native Places alias: the VPS GooglePlacesProvider POSTs /v1/places:searchText etc.
        if (url.pathname.startsWith("/v1/")) return placesProxy(url, request, env, ctx, "");
        return json({ ok: false, error: err("not_found", `no route ${path}`, false) }, 404);
      }

      return json({ ok: false, error: err("method_not_allowed", request.method, false) }, 405);
    } catch (e) {
      // Details go to the worker logs (observability), never to the response: even though every
      // caller is our own authenticated VPS, error internals don't belong on the wire.
      console.error("search-worker unhandled error:", e);
      return json({ ok: false, error: err("internal_error", "unexpected failure — see worker logs", true) }, 500);
    }
  },
};

// ---------------------------------------------------------------------------
// Auth (house pattern — identical to workers/scrape-worker)
// ---------------------------------------------------------------------------

/** Returns a 401/500 Response if the request is not authorized, otherwise null. */
function authorize(request, env) {
  const expected = env.AUTH_TOKEN;
  if (!expected) {
    // Fail closed: a missing secret must never mean "open to the world".
    return json({ ok: false, error: err("server_misconfigured", "AUTH_TOKEN secret is not set", false) }, 500);
  }

  const header = request.headers.get("Authorization") || "";
  let presented = null;

  if (header.startsWith("Bearer ")) {
    presented = header.slice("Bearer ".length).trim();
  } else if (header.startsWith("Basic ")) {
    try {
      const decoded = atob(header.slice("Basic ".length).trim());
      presented = decoded.slice(decoded.indexOf(":") + 1);
    } catch {
      presented = null;
    }
  }

  if (presented && timingSafeEqual(presented, expected)) return null;
  // Rotation grace: the VPS token authority rotates bearers with a grace window —
  // the previous token stays valid until the next rotation, so in-flight callers
  // and the app's cached clients never 401 mid-rotation. Unset is the normal case.
  if (presented && env.AUTH_TOKEN_PREVIOUS && timingSafeEqual(presented, env.AUTH_TOKEN_PREVIOUS)) return null;

  return new Response(JSON.stringify({ ok: false, error: err("unauthorized", "bad or missing token", false) }), {
    status: 401,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "WWW-Authenticate": 'Basic realm="daleel-search", charset="UTF-8"',
      ...corsHeaders(),
    },
  });
}

/** Length-independent constant-time string compare. */
function timingSafeEqual(a, b) {
  const ab = new TextEncoder().encode(a);
  const bb = new TextEncoder().encode(b);
  let diff = ab.length ^ bb.length;
  const len = Math.max(ab.length, bb.length);
  for (let i = 0; i < len; i++) {
    diff |= (ab[i] || 0) ^ (bb[i] || 0);
  }
  return diff === 0;
}

// ---------------------------------------------------------------------------
// Routes
// ---------------------------------------------------------------------------

function root() {
  return json({
    service: "daleel-search-worker",
    role: "search execution host (Phase A: SerpAPI + Google Places proxied verbatim, KV-cached)",
    endpoints: {
      "GET /health": "liveness",
      "GET /serpapi/search?<params>": "proxy to serpapi.com/search.json (worker injects api_key; a caller-sent one is stripped)",
      "GET /search.json?<params>": "provider-native alias of /serpapi/search",
      "GET|POST /v1/{path}": "provider-native alias of /places/{path}",
      "POST /places/{path}": "proxy to places.googleapis.com/{path} (X-Goog-Api-Key injected, X-Goog-FieldMask passed through)",
    },
  });
}

/**
 * GET /serpapi/search — forward the caller's query (minus api_key, which we inject from the
 * SERPAPI_KEY secret) to SerpAPI and return the vendor's JSON verbatim with its status code.
 * 200s are KV-cached under a canonical key so hot repeats short-circuit at the edge.
 */
async function serpApiSearch(url, env, ctx) {
  // The VPS SerpApiProvider ALWAYS attaches an api_key (its real key, or the "edge-proxied"
  // placeholder when the key lives only here) — strip it unconditionally: the worker's secret is
  // the only key that ever reaches the vendor, and the caller's value must not affect the cache key.
  url.searchParams.delete("api_key");

  const key = env.SERPAPI_KEY;
  if (!key) {
    // Fail closed — same stance as auth: misconfiguration is a 500, never a pass-through.
    return json({ ok: false, error: err("server_misconfigured", "SERPAPI_KEY secret is not set", false) }, 500);
  }

  // Canonical cache key: params sorted by name (then value, for repeated names) so semantically
  // identical queries hash identically regardless of the order the VPS serialized them in.
  const sorted = [...url.searchParams.entries()].sort(
    (a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : a[1] < b[1] ? -1 : a[1] > b[1] ? 1 : 0),
  );
  const canonical = new URLSearchParams(sorted).toString();
  const hash = await sha256Hex(canonical);
  const cacheKey = "serp:v1:" + hash;
  // Long-lived shadow copy so a request served AFTER the hourly cap trips can fall back to a
  // slightly-stale SERP instead of an empty one (see the cap handling below).
  const staleKey = "serpstale:v1:" + hash;

  const hit = await kvGet(env, cacheKey);
  if (hit !== null) return vendorResponse(hit, 200, "hit");

  // Account-wide SerpAPI hourly cap (Durable Object). Consulted ONLY after a cache miss, so cache
  // hits never spend budget or pay the DO round-trip. Fail OPEN — a limiter fault must never take
  // search down (a limiter that faults every search is worse than an occasional vendor overage).
  const verdict = await reserveSerpBudget(env);
  const usage = serpUsageHeader(verdict);
  if (verdict && verdict.allowed === false) {
    const cappedHeaders = { ...usage, "X-SerpApi-Capped": "true" };
    const stale = await kvGet(env, staleKey);
    // Prefer a stale cached SERP; else a VALID-but-empty SerpAPI body at HTTP 200 so the VPS provider
    // degrades to zero results for this query rather than faulting on a non-2xx / 429 (an empty
    // "no results" from a thrown error is a known Daleel failure mode — the soft body avoids it).
    return stale !== null
      ? vendorResponse(stale, 200, "stale", null, cappedHeaders)
      : vendorResponse(cappedSerpBody(), 200, "capped", null, cappedHeaders);
  }

  const target = new URL(SERPAPI_BASE + "/search.json"); // .json = the provider-native endpoint
  for (const [name, value] of sorted) target.searchParams.append(name, value);
  target.searchParams.set("api_key", key);

  let res, body;
  try {
    res = await fetch(target, { method: "GET" });
    body = await res.text();
  } catch (e) {
    // No SERP was consumed — refund the reserved slot so a transient outage (which the VPS retries)
    // doesn't inflate the hourly counter with calls that never billed the vendor.
    await releaseSerpBudget(env, verdict);
    throw e;
  }

  if (res.status === 200) {
    kvPut(env, ctx, cacheKey, body, serpTtlSeconds(env));
    kvPut(env, ctx, staleKey, body, STALE_TTL_SECONDS);
  } else {
    // Non-200 = no billable SERP; refund the slot so only successful vendor calls count toward the cap.
    await releaseSerpBudget(env, verdict);
  }

  return vendorResponse(body, res.status, "miss", res.headers.get("Content-Type"), usage);
}

/**
 * POST /places/{...} — forward the remaining path (+ any query string) and the JSON body to the
 * Google Places API, injecting X-Goog-Api-Key from the GOOGLE_PLACES_API_KEY secret and passing
 * the caller's X-Goog-FieldMask through (the field mask changes the response shape, so it is
 * part of the cache identity).
 */
async function placesProxy(url, request, env, ctx, prefix) {
  const key = env.GOOGLE_PLACES_API_KEY;
  if (!key) {
    return json({ ok: false, error: err("server_misconfigured", "GOOGLE_PLACES_API_KEY secret is not set", false) }, 500);
  }

  // Everything after the route prefix ("" for the provider-native /v1/* alias) — always begins
  // with "/" by the route guards. Appended to a full origin, so no path value can redirect the
  // request off places.googleapis.com.
  const forwardPath = url.pathname.slice(prefix.length) + url.search;
  if (forwardPath === "/" || forwardPath.startsWith("/?")) {
    return json({ ok: false, error: err("bad_request", "missing Places API path", false) }, 400);
  }

  const body = request.method === "GET" ? "" : await request.text();
  if (body.length > 0) {
    try {
      JSON.parse(body);
    } catch {
      return json({ ok: false, error: err("bad_request", "body must be JSON", false) }, 400);
    }
  }

  const fieldMask = request.headers.get("X-Goog-FieldMask") || "";

  // Cache identity = path + body + fieldmask (newline-delimited so no two distinct triples can
  // concatenate to the same string). Body is hashed byte-for-byte — the VPS provider serializes
  // deterministically, so identical requests produce identical keys.
  const cacheKey = "places:v1:" + (await sha256Hex(forwardPath + "\n" + body + "\n" + fieldMask));

  const hit = await kvGet(env, cacheKey);
  if (hit !== null) return vendorResponse(hit, 200, "hit");

  const res = await fetch(PLACES_BASE + forwardPath, {
    method: request.method,
    headers: {
      "X-Goog-Api-Key": key,
      ...(body.length > 0 ? { "Content-Type": "application/json" } : {}),
      ...(fieldMask ? { "X-Goog-FieldMask": fieldMask } : {}),
    },
    ...(body.length > 0 ? { body } : {}),
  });
  const text = await res.text();

  if (res.status === 200) kvPut(env, ctx, cacheKey, text, PLACES_TTL_SECONDS);

  return vendorResponse(text, res.status, "miss", res.headers.get("Content-Type"));
}

// ---------------------------------------------------------------------------
// KV cache — best-effort accelerator, never a failure source
// ---------------------------------------------------------------------------

/** Configurable SerpAPI TTL: CACHE_TTL_SECONDS var, default 3600, clamped to the KV minimum of 60. */
function serpTtlSeconds(env) {
  const n = parseInt(env.CACHE_TTL_SECONDS, 10);
  const ttl = Number.isFinite(n) && n > 0 ? n : 3600;
  return Math.max(ttl, MIN_TTL_SECONDS);
}

/** KV read that degrades to a cache miss on any failure (KV is an accelerator, not a dependency). */
async function kvGet(env, key) {
  try {
    return await env.SEARCH_CACHE.get(key, { type: "text" });
  } catch (e) {
    console.error("search-worker KV get failed (treating as miss):", e);
    return null;
  }
}

/**
 * Fire-and-forget KV write via waitUntil — the vendor response is never delayed or failed by the
 * cache. Only ever called for 200 bodies (non-200s must not be cached).
 */
function kvPut(env, ctx, key, body, ttlSeconds) {
  ctx.waitUntil(
    env.SEARCH_CACHE.put(key, body, { expirationTtl: ttlSeconds }).catch((e) => {
      console.error("search-worker KV put failed (response already served):", e);
    }),
  );
}

// ---------------------------------------------------------------------------
// Utils
// ---------------------------------------------------------------------------

async function sha256Hex(s) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/**
 * The vendor's body verbatim (NOT the {ok,...} envelope — the VPS providers parse the raw vendor
 * JSON exactly as they did against the vendor directly), tagged with the cache disposition.
 */
function vendorResponse(body, status, cacheDisposition, contentType = null, extraHeaders = {}) {
  return new Response(body, {
    status,
    headers: {
      "Content-Type": contentType || "application/json; charset=utf-8",
      "X-Cache": cacheDisposition,
      ...extraHeaders,
      ...corsHeaders(),
    },
  });
}

// ---------------------------------------------------------------------------
// SerpAPI hourly cap (Durable Object) — account-wide rolling-window rate limit
// ---------------------------------------------------------------------------

/** Resolves the hourly limit from SERPAPI_HOURLY_LIMIT, falling back to the default. */
function serpHourlyLimit(env) {
  const n = parseInt(env.SERPAPI_HOURLY_LIMIT, 10);
  return Number.isFinite(n) && n > 0 ? n : DEFAULT_SERPAPI_HOURLY_LIMIT;
}

/**
 * Reserves one SerpAPI slot from the single global Durable Object. Returns the DO's verdict
 * ({allowed, used, limit, remaining, resetAt}), or null when the limiter is not configured or errors
 * — null means "not enforced", i.e. FAIL OPEN so the limiter can never take search down.
 */
async function reserveSerpBudget(env) {
  if (!env.SERP_LIMITER) return null;
  try {
    const id = env.SERP_LIMITER.idFromName("serpapi-global");
    const stub = env.SERP_LIMITER.get(id);
    const res = await stub.fetch("https://do/reserve", { method: "POST" });
    return await res.json();
  } catch (e) {
    console.error("search-worker SerpAPI limiter failed (allowing):", e);
    return null;
  }
}

/**
 * Refunds a slot reserved for a call that then failed to produce a billable SERP. No-op unless a slot
 * was actually taken (verdict.allowed === true) — so a disabled or failed-open limiter never
 * under-counts by releasing a slot it never reserved.
 */
async function releaseSerpBudget(env, verdict) {
  if (!env.SERP_LIMITER || !verdict || verdict.allowed !== true) return;
  try {
    const id = env.SERP_LIMITER.idFromName("serpapi-global");
    const stub = env.SERP_LIMITER.get(id);
    await stub.fetch("https://do/release", { method: "POST" });
  } catch (e) {
    console.error("search-worker SerpAPI limiter release failed (slot stays counted):", e);
  }
}

/** Renders the DO verdict as an X-SerpApi-Usage header value (empty object when not enforced). */
function serpUsageHeader(verdict) {
  if (!verdict) return {};
  const parts = [`used=${verdict.used}`, `limit=${verdict.limit}`, `remaining=${verdict.remaining}`];
  if (verdict.resetAt) parts.push(`resetAt=${new Date(verdict.resetAt).toISOString()}`);
  return { "X-SerpApi-Usage": parts.join("; ") };
}

/**
 * A minimal but structurally-VALID SerpAPI response body with every result array empty. The VPS
 * SerpApiProvider parses these arrays and yields zero results (its paged loop then breaks cleanly),
 * so a capped request degrades to "no results for this query" instead of a thrown ProviderException.
 */
function cappedSerpBody() {
  return JSON.stringify({
    search_metadata: { status: "Capped" },
    organic_results: [],
    shopping_results: [],
    local_results: [],
    images_results: [],
    news_results: [],
  });
}

/**
 * The account-wide SerpAPI rate limiter. One instance (idFromName("serpapi-global")) funnels every
 * cache-missed SerpAPI call, giving a strongly-consistent rolling-hour counter that KV — eventually
 * consistent, last-write-wins — cannot provide. Storage holds the timestamps of the hits still inside
 * the window (bounded by limit + 1h). The read-modify-write is serialized through an in-memory promise
 * chain so two concurrent /reserve calls can't both read the pre-increment count.
 */
export class SerpApiRateLimiter {
  constructor(state, env) {
    this.state = state;
    this.env = env;
    this._chain = Promise.resolve();
  }

  async fetch(request) {
    const release = new URL(request.url).pathname === "/release";
    // Serialize onto the chain so overlapping reserves/releases run strictly one-after-another.
    const run = this._chain.then(() =>
      release ? this._release() : this._reserve(serpHourlyLimit(this.env)));
    this._chain = run.catch(() => {}); // keep the chain alive even if one op throws
    const result = await run;
    return new Response(JSON.stringify(result), {
      headers: { "Content-Type": "application/json" },
    });
  }

  /**
   * Refunds one previously-reserved slot — used when a reserved call produced NO billable SERP (a
   * vendor 5xx / network error the worker doesn't cache, which the VPS then retries). Without this,
   * one transient outage would burn up to 3 slots per query (reserve + 2 retries) and prematurely
   * trip the cap. Entries are interchangeable (we only track the count), so popping the newest is fine.
   */
  async _release() {
    const hits = (await this.state.storage.get("hits")) || [];
    if (hits.length > 0) {
      hits.pop();
      await this.state.storage.put("hits", hits);
    }
    return { released: true, used: hits.length };
  }

  async _reserve(limit) {
    const now = Date.now();
    const windowStart = now - 3_600_000;
    let hits = (await this.state.storage.get("hits")) || [];
    hits = hits.filter((t) => t > windowStart); // drop everything older than the 1h window
    const oldest = hits.length > 0 ? hits[0] : now;

    if (hits.length >= limit) {
      return { allowed: false, used: hits.length, limit, remaining: 0, resetAt: oldest + 3_600_000 };
    }

    hits.push(now);
    await this.state.storage.put("hits", hits);
    return {
      allowed: true,
      used: hits.length,
      limit,
      remaining: Math.max(0, limit - hits.length),
      resetAt: hits[0] + 3_600_000,
    };
  }
}

function err(code, message, retryable) {
  return { code, message, retryable };
}

function corsHeaders() {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Access-Control-Allow-Headers": "Authorization, Content-Type, X-Goog-FieldMask",
    "Access-Control-Expose-Headers": "X-Cache, X-SerpApi-Usage, X-SerpApi-Capped",
    "Access-Control-Max-Age": "86400",
  };
}

function json(body, status = 200) {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", ...corsHeaders() },
  });
}
