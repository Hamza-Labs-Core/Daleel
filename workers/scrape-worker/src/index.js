/**
 * Daleel scrape-worker
 * --------------------------------------------------------------------------
 * The scraping EXECUTION HOST of docs/architecture/cloudflare-workers-pipeline.md
 * (§3.1), Phase 1a: the existing Context.dev calls relocated to the edge verbatim.
 * This is deliberately NOT a vendor swap — the win is the execution shape:
 * async, queue-backed, horizontally parallel, and durable-by-construction
 * (results land in R2 the moment they exist, so a VPS-side timeout can never
 * discard a finished crawl again).
 *
 * Endpoints (all require auth — house pattern, same as log-viewer):
 *   GET  /                -> service banner + endpoint list (JSON)
 *   GET  /health          -> liveness
 *   GET  /jobs/{jobId}    -> async job status (JobStatusResponse)
 *   POST /scrape/page     -> sync: one page via Context.dev markdown/html scrape
 *   POST /scrape/catalog  -> async: a store's product catalogue (Context.dev
 *                            /v1/brand/ai/products) — 202 {jobId, resultKey}
 *   POST /scrape/brand    -> async: brand profile (+ optional catalogue)
 *
 * Async flow: submit -> enqueue onto SCRAPE_QUEUE -> this worker's queue()
 * consumer does the vendor call (queue handlers get a long execution budget,
 * unlike waitUntil's ~30s grace) -> result JSON to R2 -> status doc to R2 ->
 * thin PollMessage onto POLL_QUEUE for the VPS pull consumer. Retryable vendor
 * failures throw so Queues redeliver (max_retries -> DLQ); terminal failures
 * write an error status + poll message so the VPS surfaces a FAULTED run, never
 * a silently empty one.
 *
 * Idempotency: jobId = caller's Idempotency-Key or a SHA-256 of the body; the
 * result key is deterministic per jobId, and a re-submit whose result already
 * exists short-circuits to "done" without re-running (at-least-once safe) —
 * UNLESS the submit body carries refresh=true or maxAgeSeconds=N, which forces a
 * re-run of a stale result (brand harvests have an eternal resultKey and must be
 * re-crawlable on a TTL). A short-lived '.inflight' R2 marker additionally stops a
 * queued/running jobId being enqueued twice (head() only detects FINISHED jobs).
 */

const CONTEXT_DEV_BASE = "https://api.context.dev";

/** Default server-side budget for one Context.dev catalogue extraction. */
const DEFAULT_CATALOG_TIMEOUT_MS = 120_000;

/** Ceiling on the poll deadline a submit may request (ms). */
const MAX_DEADLINE_MS = 30 * 60 * 1000;

/**
 * How long a '.inflight' marker is honoured before it's treated as stale (ms). A crashed/dead job
 * whose marker was never cleared must not wedge a jobId forever, so the guard self-heals: past this
 * age the marker is ignored and the submit proceeds. Comfortably longer than a normal catalogue run.
 */
const INFLIGHT_TTL_MS = 10 * 60 * 1000;

/**
 * Total deliveries per work message: 1 initial + max_retries (3) from wrangler.jsonc's
 * [[queues.consumers]]. KEEP IN SYNC — on the final delivery the job is finished as a terminal
 * error instead of retried, so the VPS always learns the outcome (never a status stuck "running").
 */
const MAX_DELIVERIES = 4;

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
        if (path.startsWith("/jobs/")) return jobStatus(path.slice("/jobs/".length), env);
        return json({ ok: false, error: err("not_found", `no route ${path}`, false) }, 404);
      }

      if (request.method === "POST") {
        const body = await readJson(request);
        if (body.error) return json({ ok: false, error: err("bad_request", body.error, false) }, 400);

        if (path === "/scrape/page") return scrapePage(body.value, env);
        if (path === "/scrape/catalog") return submitAsync("catalog", body.value, request, env);
        if (path === "/scrape/brand") return submitAsync("brand", body.value, request, env);
        return json({ ok: false, error: err("not_found", `no route ${path}`, false) }, 404);
      }

      return json({ ok: false, error: err("method_not_allowed", request.method, false) }, 405);
    } catch (e) {
      // Details go to the worker logs (observability), never to the response: even though every
      // caller is our own authenticated VPS, error internals don't belong on the wire.
      console.error("scrape-worker unhandled error:", e);
      return json({ ok: false, error: err("internal_error", "unexpected failure — see worker logs", true) }, 500);
    }
  },

  /**
   * Push consumer for SCRAPE_QUEUE: does the actual vendor call for one submitted job.
   * A retryable failure calls msg.retry() (Queues redeliver, then DLQ); success and
   * terminal failures ack. Either way the VPS always learns the outcome via POLL_QUEUE.
   */
  async queue(batch, env, ctx) {
    for (const msg of batch.messages) {
      try {
        await processJob(msg.body, env);
        msg.ack();
      } catch (e) {
        if (e instanceof TerminalJobError) {
          // Vendor rejected the job for good — record the failure and don't burn retries. The
          // recording itself can fail transiently (R2/queue blip): retry just this message then,
          // never let the throw escape queue() and re-drive the whole batch.
          try {
            await finishJob(msg.body, env, { error: err(e.code, e.message, false) });
            msg.ack();
          } catch {
            msg.retry({ delaySeconds: Math.min(60, 10 * (msg.attempts || 1)) });
          }
        } else if ((msg.attempts || 1) >= MAX_DELIVERIES) {
          // Final delivery — retrying would dead-letter the job with the status doc stuck on
          // "running" and no poll message, i.e. a silent gap. Finish it as a terminal error so
          // the VPS surfaces a real failure (the drain's !IsDone branch); the DLQ stays reserved
          // for genuinely unprocessable messages (finishJob itself failing below).
          try {
            await finishJob(msg.body, env, { error: err("retries_exhausted", String((e && e.message) || e), true) });
            msg.ack();
          } catch {
            msg.retry(); // exhausts → DLQ; depth alarms are the last-resort operator signal
          }
        } else {
          // Transient (network/5xx/timeout): let Queues redeliver with backoff.
          msg.retry({ delaySeconds: Math.min(60, 10 * (msg.attempts || 1)) });
        }
      }
    }
  },
};

// ---------------------------------------------------------------------------
// Auth (house pattern — identical to workers/log-viewer)
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
      "WWW-Authenticate": 'Basic realm="daleel-scrape", charset="UTF-8"',
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
    service: "daleel-scrape-worker",
    role: "scraping execution host (Phase 1a: Context.dev relocated, unchanged)",
    endpoints: {
      "GET /health": "liveness",
      "GET /jobs/{jobId}": "async job status",
      "POST /scrape/page": "sync one-page scrape { url, format? }",
      "POST /scrape/catalog": "async store catalogue { domain, maxProducts?, timeoutMs?, searchJobId?, store? }",
      "POST /scrape/brand": "async brand profile { domain, withCatalog?, searchJobId?, store? }",
    },
  });
}

/** GET /jobs/{jobId} — status from the R2 status doc; "done" iff the result object exists. */
async function jobStatus(jobId, env) {
  if (!jobId) return json({ ok: false, error: err("bad_request", "missing jobId", false) }, 400);

  const status = await env.DATA_BUCKET.get(statusKey(env, jobId));
  if (!status) return json({ ok: false, error: err("job_not_found", jobId, false) }, 404);

  const doc = await status.json();
  return json({ ok: true, ...doc });
}

/** POST /scrape/page — synchronous single-page scrape via Context.dev (fast path, no queue). */
async function scrapePage(body, env) {
  if (!body || typeof body.url !== "string" || !body.url.trim()) {
    return json({ ok: false, error: err("bad_request", "url is required", false) }, 400);
  }
  const started = Date.now();
  const format = body.format === "html" ? "html" : "markdown";
  const res = await contextDev(env, "GET", `/v1/web/scrape/${format}?url=${encodeURIComponent(body.url.trim())}`);

  const content = firstString(res, "markdown", "html", "content", "text", "data") || "";
  const title = firstString(res, "title") || (res.metadata ? firstString(res.metadata, "title") : null);

  // Shape mirrors the C# ScrapedPage record so the VPS deserializes it directly.
  return json({
    ok: true,
    mode: "sync",
    backendUsed: "contextdev",
    result: {
      url: body.url.trim(),
      title,
      content,
      format: format === "html" ? "Html" : "Markdown",
      provider: "scrape-worker/context.dev",
      success: content.length > 0,
      error: content.length > 0 ? null : "empty content",
    },
    meta: { ms: Date.now() - started },
  });
}

/**
 * POST /scrape/catalog|/scrape/brand — accept, enqueue, 202. The job is processed by this
 * worker's queue consumer; the caller polls /jobs/{jobId} or drains POLL_QUEUE.
 */
async function submitAsync(kind, body, request, env) {
  if (!body || typeof body.domain !== "string" || !body.domain.trim()) {
    return json({ ok: false, error: err("bad_request", "domain is required", false) }, 400);
  }

  const domain = normalizeDomain(body.domain);
  const jobId = await deriveJobId(request, kind, body);
  const resultKey = `${keyPrefix(env)}pipeline/${sanitize(String(body.searchJobId ?? "adhoc"))}/${kind}/${jobId}.json`;

  // Freshness: a brand harvest has an ETERNAL resultKey (searchJobId:null → the jobId SHA never
  // changes), so a plain "already in R2 → done" short-circuit would freeze that catalogue forever —
  // BrandCatalogService re-submits on TTL but the worker would keep replying with the stale object.
  // Honour an explicit refresh: pass refresh=true (or maxAgeSeconds=N) to re-run when the existing
  // result is stale; without either flag the short-circuit is preserved exactly as before.
  const existing = await env.DATA_BUCKET.head(resultKey);
  if (existing && !isStale(existing, body)) {
    await putStatus(env, jobId, { status: "done", jobId, resultKey });
    return accepted(env, jobId, resultKey);
  }

  // In-flight guard: head() only sees FINISHED jobs, so re-submitting a queued/running jobId would
  // enqueue a SECOND message and run Context.dev twice. A short-lived '.inflight' marker (best-effort,
  // TTL-bounded) lets a concurrent re-submit report the running job instead of double-enqueueing.
  const inflightKey = resultKey + ".inflight";
  if (await isInflight(env, inflightKey)) {
    await putStatus(env, jobId, { status: "running", jobId, resultKey });
    return accepted(env, jobId, resultKey);
  }
  await markInflight(env, inflightKey);

  const job = {
    kind,
    jobId,
    resultKey,
    domain,
    // Uncapped by default: only forward an explicit caller-chosen cap. Cost stays bounded by
    // Context.dev's own limits, the queue's retry budget, and the VPS per-job cost cap.
    maxProducts: intOrNull(body.maxProducts),
    timeoutMs: intOrNull(body.timeoutMs) || DEFAULT_CATALOG_TIMEOUT_MS,
    withCatalog: body.withCatalog !== false,
    // Always a JSON string (or null): every VPS-side DTO types searchJobId as string, and a numeric
    // value here would fail PollMessage deserialization and silently discard the crawl.
    searchJobId: body.searchJobId == null ? null : String(body.searchJobId),
    store: typeof body.store === "string" ? body.store : null,
    // Carried so the consumer can clear the in-flight marker once the job is finished.
    inflightKey,
    enqueuedAt: Date.now(),
    deadlineAt: Date.now() + Math.min(intOrNull(body.deadlineMs) || MAX_DEADLINE_MS, MAX_DEADLINE_MS),
  };

  await putStatus(env, jobId, { status: "queued", jobId, resultKey });
  await env.SCRAPE_QUEUE.send(job);
  return accepted(env, jobId, resultKey);
}

function accepted(env, jobId, resultKey) {
  // The poll-queue NAME is per-environment metadata (prod vs qa) — never hardcode prod's.
  return json(
    { ok: true, mode: "async", jobId, resultKey, poll: { queue: env.POLL_QUEUE_NAME || "daleel-poll-work", after: 5 } },
    202,
  );
}

// ---------------------------------------------------------------------------
// Queue consumer — the actual work
// ---------------------------------------------------------------------------

/** Raised for vendor verdicts that retrying cannot fix (bad domain, auth, 4xx). */
class TerminalJobError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

async function processJob(job, env) {
  // A redelivered message whose result already landed is a no-op (at-least-once safety).
  if (await env.DATA_BUCKET.head(job.resultKey)) {
    await finishJob(job, env, {});
    return;
  }

  await putStatus(env, job.jobId, { status: "running", jobId: job.jobId, resultKey: job.resultKey });

  const started = Date.now();
  let result;
  if (job.kind === "catalog") {
    result = await extractCatalog(job, env);
  } else if (job.kind === "brand") {
    result = await retrieveBrand(job, env);
  } else {
    throw new TerminalJobError("bad_request", `unknown job kind '${job.kind}'`);
  }

  // R2 first, then status, then the poll pointer — the result is durable before anyone
  // is told about it, so every consumer that hears "done" can immediately read it.
  await env.DATA_BUCKET.put(job.resultKey, JSON.stringify(result), {
    httpMetadata: { contentType: "application/json; charset=utf-8" },
  });
  await finishJob(job, env, { ms: Date.now() - started });
}

/** Writes the terminal status doc and enqueues the poll pointer (success or terminal error). */
async function finishJob(job, env, { error = null, ms = null } = {}) {
  // Job is terminal (success or terminal error): clear the in-flight marker so the next submit is
  // gated by the freshness check on the result, not by a stale marker. Best-effort — a leftover
  // marker just ages out via its TTL, so a failed delete can never wedge a jobId.
  await clearInflight(env, job.inflightKey);

  await putStatus(env, job.jobId, {
    status: error ? "error" : "done",
    jobId: job.jobId,
    resultKey: job.resultKey,
    error,
    meta: ms === null ? undefined : { ms },
  });

  await env.POLL_QUEUE.send({
    type: "poll",
    worker: "scrape",
    kind: job.kind,
    jobId: job.jobId,
    resultKey: job.resultKey,
    searchJobId: job.searchJobId,
    store: job.store,
    domain: job.domain,
    status: error ? "error" : "done",
    error: error ? error.message : null,
    enqueuedAt: job.enqueuedAt,
    deadlineAt: job.deadlineAt,
  });
}

/** The Phase 1a call, verbatim: Context.dev POST /v1/brand/ai/products. */
async function extractCatalog(job, env) {
  const payload = { domain: job.domain, timeoutMS: job.timeoutMs };
  // Only cap when the caller asked for a cap — the default is the vendor's own ceiling.
  if (job.maxProducts && job.maxProducts > 0) payload.maxProducts = job.maxProducts;

  const res = await contextDev(env, "POST", "/v1/brand/ai/products", payload);
  const products = Array.isArray(res.products) ? res.products.map(normalizeProduct).filter(Boolean) : [];

  return {
    kind: "catalog",
    domain: job.domain,
    store: job.store,
    searchJobId: job.searchJobId,
    capturedAt: new Date().toISOString(),
    productCount: products.length,
    products,
  };
}

/** Brand profile (GET /v1/brand/retrieve) plus, by default, its catalogue in the same job. */
async function retrieveBrand(job, env) {
  const res = await contextDev(env, "GET", `/v1/brand/retrieve?domain=${encodeURIComponent(job.domain)}`);
  const root = res.data || res;

  const brand = {
    domain: job.domain,
    name: firstString(root, "name", "title"),
    description: firstString(root, "description", "summary"),
    industry: firstString(root, "industry", "category"),
    logoUrl: firstString(root, "logo", "logoUrl", "icon"),
  };

  let products = [];
  if (job.withCatalog) {
    const cat = await extractCatalog(job, env);
    products = cat.products;
  }

  return {
    kind: "brand",
    domain: job.domain,
    store: job.store,
    searchJobId: job.searchJobId,
    capturedAt: new Date().toISOString(),
    brand,
    productCount: products.length,
    products,
  };
}

/** One CatalogProduct in the exact camelCase shape the VPS deserializes (case-insensitive). */
function normalizeProduct(p) {
  const name = firstString(p, "name");
  if (!name) return null;
  return {
    name,
    description: firstString(p, "description"),
    price: typeof p.price === "number" ? p.price : null,
    currency: firstString(p, "currency"),
    url: firstString(p, "url"),
    category: firstString(p, "category"),
    imageUrl: firstString(p, "image_url", "imageUrl"),
    sku: firstString(p, "sku"),
  };
}

// ---------------------------------------------------------------------------
// Context.dev client
// ---------------------------------------------------------------------------

/**
 * One Context.dev call. 4xx (except 429) is terminal — retrying an invalid domain or a
 * revoked key can never succeed; 429/5xx/network throw plain errors so the queue retries.
 */
async function contextDev(env, method, pathAndQuery, body = null) {
  const key = env.CONTEXT_DEV_API_KEY;
  if (!key) throw new TerminalJobError("server_misconfigured", "CONTEXT_DEV_API_KEY secret is not set");

  const res = await fetch(CONTEXT_DEV_BASE + pathAndQuery, {
    method,
    headers: {
      Authorization: `Bearer ${key}`,
      ...(body ? { "Content-Type": "application/json" } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });

  if (!res.ok) {
    const text = (await res.text()).slice(0, 500);
    if (res.status >= 400 && res.status < 500 && res.status !== 429) {
      throw new TerminalJobError("BACKEND_ERROR", `context.dev ${res.status}: ${text}`);
    }
    throw new Error(`context.dev ${res.status}: ${text}`); // retryable
  }

  return res.json();
}

// ---------------------------------------------------------------------------
// R2 status docs & keys
// ---------------------------------------------------------------------------

/** Env-scoped key prefix, e.g. "prod/" or "qa/" — extra isolation on top of per-env buckets. */
function keyPrefix(env) {
  // Trailing slashes stripped with a loop, not a regex — an anchored /\/+$/ on configurable input
  // is CodeQL's classic polynomial-backtracking flag, and the loop is just as clear.
  let p = (env.ENV_PREFIX || "").trim();
  while (p.endsWith("/")) p = p.slice(0, -1);
  return p ? `${p}/` : "";
}

function statusKey(env, jobId) {
  return `${keyPrefix(env)}jobs/${jobId}.json`;
}

async function putStatus(env, jobId, doc) {
  await env.DATA_BUCKET.put(statusKey(env, jobId), JSON.stringify({ ...doc, updatedAt: Date.now() }), {
    httpMetadata: { contentType: "application/json; charset=utf-8" },
  });
}

// ---------------------------------------------------------------------------
// Freshness & in-flight guard (FIX: brand catalogues froze; double-enqueue)
// ---------------------------------------------------------------------------

/**
 * True when an EXISTING finished result should be re-run instead of short-circuited. Opt-in only:
 * the caller must pass refresh=true or a positive maxAgeSeconds, so unflagged submits keep the old
 * "already in R2 → done" behaviour exactly. `head()` exposes R2's own `uploaded` timestamp, so the
 * age check needs no bytes read.
 */
function isStale(existing, body) {
  if (body && body.refresh === true) return true;
  const maxAgeSeconds = intOrNull(body && body.maxAgeSeconds);
  if (!maxAgeSeconds) return false;
  const uploadedMs = existing && existing.uploaded ? new Date(existing.uploaded).getTime() : NaN;
  if (!Number.isFinite(uploadedMs)) return false; // unknown age ⇒ treat as fresh (never re-run blindly)
  return Date.now() - uploadedMs > maxAgeSeconds * 1000;
}

/**
 * True when a submit for this jobId is already queued/running (its '.inflight' marker exists and is
 * still within its TTL). Best-effort: any R2 hiccup returns false so a submit is never blocked by the
 * guard — the worst case degrades to the pre-fix behaviour (a possible double-enqueue), never a lost job.
 */
async function isInflight(env, inflightKey) {
  try {
    const marker = await env.DATA_BUCKET.get(inflightKey);
    if (!marker) return false;
    const at = Number((await marker.text()) || 0);
    if (Number.isFinite(at) && Date.now() - at < INFLIGHT_TTL_MS) return true;
    return false; // stale marker: let this submit proceed (self-heals a crashed job)
  } catch {
    return false;
  }
}

/** Writes the '.inflight' marker (timestamp body). Best-effort — a failed write just loses the guard. */
async function markInflight(env, inflightKey) {
  try {
    await env.DATA_BUCKET.put(inflightKey, String(Date.now()), {
      httpMetadata: { contentType: "text/plain; charset=utf-8" },
    });
  } catch {
    /* best-effort */
  }
}

/** Clears the '.inflight' marker when a job is finished. Best-effort — a leftover just ages out. */
async function clearInflight(env, inflightKey) {
  if (!inflightKey) return;
  try {
    await env.DATA_BUCKET.delete(inflightKey);
  } catch {
    /* best-effort */
  }
}

// ---------------------------------------------------------------------------
// Utils
// ---------------------------------------------------------------------------

/** jobId: the caller's Idempotency-Key, else a SHA-256 of (kind + canonical body). */
async function deriveJobId(request, kind, body) {
  const provided = (request.headers.get("Idempotency-Key") || "").trim();
  if (provided) return sanitize(provided).slice(0, 96);

  // `store` is part of the identity: two stores that resolve to the same domain in one search must
  // get distinct jobs/resultKeys, or the second store's prices silently vanish (its submit would
  // short-circuit on the first store's finished result and never produce a poll message).
  const canonical = JSON.stringify({
    kind,
    domain: body.domain,
    searchJobId: body.searchJobId == null ? null : String(body.searchJobId),
    store: typeof body.store === "string" ? body.store : null,
    maxProducts: body.maxProducts ?? null,
    withCatalog: body.withCatalog !== false,
  });
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonical));
  return [...new Uint8Array(digest)].slice(0, 16).map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Bare registrable domain (drops scheme + leading www) — mirrors the VPS DomainOf helper. */
function normalizeDomain(raw) {
  let s = raw.trim();
  if (!s.includes("://")) s = `https://${s}`;
  try {
    const host = new URL(s).host;
    return host.toLowerCase().startsWith("www.") ? host.slice(4) : host;
  } catch {
    return raw.trim();
  }
}

function sanitize(s) {
  return s.replace(/[^a-zA-Z0-9._-]+/g, "-");
}

function intOrNull(v) {
  const n = parseInt(v, 10);
  return Number.isFinite(n) && n > 0 ? n : null;
}

function firstString(obj, ...keys) {
  if (!obj || typeof obj !== "object") return null;
  for (const k of keys) {
    const v = obj[k];
    if (typeof v === "string" && v.length) return v;
  }
  return null;
}

async function readJson(request) {
  try {
    return { value: await request.json() };
  } catch {
    return { error: "body must be JSON" };
  }
}

function err(code, message, retryable) {
  return { code, message, retryable };
}

function corsHeaders() {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Access-Control-Allow-Headers": "Authorization, Content-Type, Idempotency-Key",
    "Access-Control-Max-Age": "86400",
  };
}

function json(body, status = 200) {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", ...corsHeaders() },
  });
}
