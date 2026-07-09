/**
 * Daleel classify-worker
 * --------------------------------------------------------------------------
 * The CLASSIFICATION execution host of docs/architecture/cloudflare-workers-pipeline.md
 * (§3.2): commodity text/image labeling on Workers AI — buy-intent heuristics, category
 * tagging, the keyword/LLM adjudication *signal* inside moderation. NOT strategy
 * planning or nuanced analysis (those stay on Claude/OpenRouter), and NOT the
 * moderation authority: this is a stateless classifier; callers own thresholds
 * and policy.
 *
 * Endpoints (all require auth — house pattern, same as scrape-worker/log-viewer):
 *   GET  /                 -> service banner + endpoint list (JSON)
 *   GET  /health           -> liveness
 *   POST /classify/text    -> { items:[{id,text}], labels:[...], model? }
 *                             -> { verdicts:[{id,label,confidence,reason}] }
 *   POST /classify/images  -> { urls:[...], prompt?, model? }
 *                             -> { verdicts:[{url,label,confidence}] }
 *
 * Mode: sync per batch (≤100 text items / ≤20 image urls per request — 413 beyond).
 * Per-item error tolerance: one failed model call yields an error verdict
 * ({label:null, confidence:0}), never a failed batch.
 *
 * Structured output: JSON mode (response_format: {type:"json_schema"}) so there is
 * no brittle text parsing; if the model/binding rejects response_format, the call
 * is retried plain and the first JSON object is scanned out of the raw response
 * (linear brace scan — no regex on external input).
 *
 * COST: Workers AI bills even in `wrangler dev` (no local mock). Small models
 * (3B/8B) are cheap; 70B burns ~8x the neurons — keep the default unless the
 * caller explicitly opts up. VPS-side metering happens in ProviderApi.
 */

const DEFAULT_TEXT_MODEL = "@cf/meta/llama-3.2-3b-instruct";
const DEFAULT_VISION_MODEL = "@cf/meta/llama-3.2-11b-vision-instruct";
const DEFAULT_IMAGE_PROMPT = "Describe the main subject in one short label.";

const MAX_TEXT_ITEMS = 100;
const MAX_LABELS = 100;
const MAX_IMAGE_URLS = 20;
const MAX_IMAGE_BYTES = 4 * 1024 * 1024; // reject image bodies beyond 4 MiB
const IMAGE_FETCH_TIMEOUT_MS = 15_000;

// Vision inputs are passed as number[] (the Workers AI image format), which is memory-heavy
// for multi-MB images — keep image concurrency low; text calls are tiny, so more lanes.
const TEXT_CONCURRENCY = 5;
const IMAGE_CONCURRENCY = 2;

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
        return json({ ok: false, error: err("not_found", `no route ${path}`, false) }, 404);
      }

      if (request.method === "POST") {
        const body = await readJson(request);
        if (body.error) return json({ ok: false, error: err("bad_request", body.error, false) }, 400);

        if (path === "/classify/text") return classifyText(body.value, env);
        if (path === "/classify/images") return classifyImages(body.value, env);
        return json({ ok: false, error: err("not_found", `no route ${path}`, false) }, 404);
      }

      return json({ ok: false, error: err("method_not_allowed", request.method, false) }, 405);
    } catch (e) {
      // Details go to the worker logs (observability), never to the response: even though every
      // caller is our own authenticated VPS, error internals don't belong on the wire.
      console.error("classify-worker unhandled error:", e);
      return json({ ok: false, error: err("internal_error", "unexpected failure — see worker logs", true) }, 500);
    }
  },
};

// ---------------------------------------------------------------------------
// Auth (house pattern — identical to workers/scrape-worker and log-viewer)
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
      "WWW-Authenticate": 'Basic realm="daleel-classify", charset="UTF-8"',
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
    service: "daleel-classify-worker",
    role: "classification execution host (Workers AI; stateless — callers own thresholds and policy)",
    endpoints: {
      "GET /health": "liveness",
      "POST /classify/text": `sync text labeling { items:[{id,text}] (max ${MAX_TEXT_ITEMS}), labels:[string,...], model? }`,
      "POST /classify/images": `sync image labeling { urls:[string,...] (max ${MAX_IMAGE_URLS}), prompt?, model? }`,
    },
  });
}

/**
 * POST /classify/text — one verdict per item. The batch never fails on a single
 * item: a failed model call yields {id, label:null, confidence:0, reason:"error"}.
 */
async function classifyText(body, env) {
  if (!body || !Array.isArray(body.items)) {
    return json({ ok: false, error: err("bad_request", "items must be an array of {id, text}", false) }, 400);
  }
  if (body.items.length > MAX_TEXT_ITEMS) {
    return json(
      { ok: false, error: err("payload_too_large", `max ${MAX_TEXT_ITEMS} items per request (got ${body.items.length})`, false) },
      413,
    );
  }
  for (let i = 0; i < body.items.length; i++) {
    const it = body.items[i];
    if (!it || typeof it !== "object" || typeof it.id !== "string" || !it.id || typeof it.text !== "string") {
      return json({ ok: false, error: err("bad_request", `items[${i}] must be { id: non-empty string, text: string }`, false) }, 400);
    }
  }

  const labels = normalizeLabels(body.labels);
  if (!labels) {
    return json(
      { ok: false, error: err("bad_request", `labels must be 1..${MAX_LABELS} non-empty strings`, false) },
      400,
    );
  }

  const model = chooseModel(body.model, DEFAULT_TEXT_MODEL);
  if (!model) {
    return json({ ok: false, error: err("bad_request", "model must be a Workers AI model id (@cf/... or @hf/...)", false) }, 400);
  }

  const started = Date.now();
  // Shared per-batch state: once one item proves the model rejects response_format,
  // the remaining items skip straight to the plain-prompt + JSON-scan fallback.
  const state = { preferPlain: false };
  const verdicts = await mapWithConcurrency(body.items, TEXT_CONCURRENCY, (item) =>
    classifyOneText(env, model, labels, item, state),
  );

  return json({
    ok: true,
    mode: "sync",
    result: { verdicts },
    meta: { ms: Date.now() - started, model, items: verdicts.length },
  });
}

/**
 * POST /classify/images — one verdict per url; per-item error tolerance. Rejected
 * urls (non-http(s)) and oversized bodies (> 4 MiB) become error verdicts, never
 * a failed batch.
 */
async function classifyImages(body, env) {
  if (!body || !Array.isArray(body.urls)) {
    return json({ ok: false, error: err("bad_request", "urls must be an array of strings", false) }, 400);
  }
  if (body.urls.length > MAX_IMAGE_URLS) {
    return json(
      { ok: false, error: err("payload_too_large", `max ${MAX_IMAGE_URLS} urls per request (got ${body.urls.length})`, false) },
      413,
    );
  }
  for (let i = 0; i < body.urls.length; i++) {
    if (typeof body.urls[i] !== "string" || !body.urls[i].trim()) {
      return json({ ok: false, error: err("bad_request", `urls[${i}] must be a non-empty string`, false) }, 400);
    }
  }
  if (body.prompt != null && typeof body.prompt !== "string") {
    return json({ ok: false, error: err("bad_request", "prompt must be a string", false) }, 400);
  }

  const model = chooseModel(body.model, DEFAULT_VISION_MODEL);
  if (!model) {
    return json({ ok: false, error: err("bad_request", "model must be a Workers AI model id (@cf/... or @hf/...)", false) }, 400);
  }

  const prompt = body.prompt && body.prompt.trim() ? body.prompt.trim() : DEFAULT_IMAGE_PROMPT;

  const started = Date.now();
  const verdicts = await mapWithConcurrency(body.urls, IMAGE_CONCURRENCY, (url) =>
    classifyOneImage(env, model, prompt, url),
  );

  return json({
    ok: true,
    mode: "sync",
    result: { verdicts },
    meta: { ms: Date.now() - started, model, items: verdicts.length },
  });
}

// ---------------------------------------------------------------------------
// Text classification — Workers AI JSON mode with plain-parse fallback
// ---------------------------------------------------------------------------

/** One text item -> {id, label, confidence, reason}. Never throws. */
async function classifyOneText(env, model, labels, item, state) {
  const messages = [
    { role: "system", content: textSystemPrompt(labels) },
    { role: "user", content: item.text },
  ];

  let raw = null;

  // Preferred path: the model's JSON mode — schema-forced, no brittle parsing.
  if (!state.preferPlain) {
    try {
      raw = await env.AI.run(model, {
        messages,
        response_format: { type: "json_schema", json_schema: verdictSchema(labels) },
        temperature: 0,
        max_tokens: 256,
      });
    } catch (e) {
      console.error(`classify/text json-mode call failed (model=${model}, id=${item.id}):`, e);
      raw = null;
    }
  }

  // Fallback: plain call, then scan the first JSON object out of the raw text. If this
  // succeeds where JSON mode threw, the binding likely rejects response_format for this
  // model — skip straight here for the rest of the batch.
  if (raw === null) {
    try {
      raw = await env.AI.run(model, { messages, temperature: 0, max_tokens: 256 });
      state.preferPlain = true;
    } catch (e) {
      console.error(`classify/text plain call failed (model=${model}, id=${item.id}):`, e);
      return { id: item.id, label: null, confidence: 0, reason: "error" };
    }
  }

  const obj = extractResponseObject(raw);
  if (!obj) return { id: item.id, label: null, confidence: 0, reason: "error" };

  const label = matchLabel(obj.label, labels);
  if (label === null) {
    // The model answered outside the provided label set — treat as a non-verdict, not a batch failure.
    return { id: item.id, label: null, confidence: 0, reason: "model returned an off-schema label" };
  }

  return {
    id: item.id,
    label,
    confidence: clamp01(obj.confidence),
    reason: typeof obj.reason === "string" ? obj.reason.slice(0, 300) : null,
  };
}

function textSystemPrompt(labels) {
  return (
    "You are a strict single-label classifier. " +
    `Allowed labels (choose EXACTLY one, verbatim): ${labels.map((l) => JSON.stringify(l)).join(", ")}. ` +
    'Respond with ONLY a JSON object of the form {"label": string, "confidence": number, "reason": string}. ' +
    '"label" MUST be one of the allowed labels. "confidence" is your certainty from 0 to 1. ' +
    '"reason" is one short sentence. No markdown, no code fences, no extra keys, no prose outside the JSON.'
  );
}

function verdictSchema(labels) {
  return {
    type: "object",
    properties: {
      label: { type: "string", enum: labels },
      confidence: { type: "number", minimum: 0, maximum: 1 },
      reason: { type: "string" },
    },
    required: ["label", "confidence", "reason"],
    additionalProperties: false,
  };
}

/**
 * Workers AI responses vary by model/mode: {response: object} (JSON mode), {response: string}
 * (plain text — may wrap the JSON in prose), or occasionally the object itself.
 */
function extractResponseObject(raw) {
  if (!raw || typeof raw !== "object") return null;
  const r = "response" in raw ? raw.response : raw;
  if (r && typeof r === "object" && !Array.isArray(r)) return r;
  if (typeof r === "string") return firstJsonObject(r);
  return null;
}

/**
 * Scans the first balanced top-level JSON object out of free text. Linear single pass
 * (string/escape aware) — deliberately NOT a regex: model output is external input and
 * a nested-brace regex is polynomial-backtracking bait.
 */
function firstJsonObject(text) {
  const start = text.indexOf("{");
  if (start < 0) return null;
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let i = start; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      if (escaped) escaped = false;
      else if (c === "\\") escaped = true;
      else if (c === '"') inString = false;
    } else if (c === '"') {
      inString = true;
    } else if (c === "{") {
      depth++;
    } else if (c === "}") {
      depth--;
      if (depth === 0) {
        try {
          const parsed = JSON.parse(text.slice(start, i + 1));
          return parsed && typeof parsed === "object" ? parsed : null;
        } catch {
          return null;
        }
      }
    }
  }
  return null;
}

/** Exact match first, then case-insensitive — always returns the caller's canonical label (or null). */
function matchLabel(candidate, labels) {
  if (typeof candidate !== "string") return null;
  if (labels.includes(candidate)) return candidate;
  const wanted = candidate.trim().toLowerCase();
  for (const l of labels) {
    if (l.toLowerCase() === wanted) return l;
  }
  return null;
}

// ---------------------------------------------------------------------------
// Image classification — fetch bytes (capped), Workers AI vision
// ---------------------------------------------------------------------------

/** Raised by fetchImageBytes with a caller-safe reason (no exception internals). */
class ImageFetchError extends Error {
  constructor(publicReason) {
    super(publicReason);
    this.publicReason = publicReason;
  }
}

/** One url -> {url, label, confidence[, reason]}. Never throws. */
async function classifyOneImage(env, model, prompt, url) {
  const target = safeHttpUrl(url);
  if (!target) {
    return { url, label: null, confidence: 0, reason: "url must be http(s)" };
  }

  let bytes;
  try {
    bytes = await fetchImageBytes(target.href);
  } catch (e) {
    if (e instanceof ImageFetchError) {
      return { url, label: null, confidence: 0, reason: e.publicReason };
    }
    console.error(`classify/images fetch failed (${url}):`, e);
    return { url, label: null, confidence: 0, reason: "image fetch failed" };
  }

  let raw;
  try {
    raw = await env.AI.run(model, {
      prompt,
      image: [...bytes], // Workers AI vision input format: number[]
      max_tokens: 128,
    });
  } catch (e) {
    console.error(`classify/images model call failed (model=${model}, url=${url}):`, e);
    return { url, label: null, confidence: 0, reason: "error" };
  }

  return imageVerdict(url, raw);
}

/** Parses only http/https urls; everything else (file:, data:, gopher:, garbage) is rejected. */
function safeHttpUrl(raw) {
  let u;
  try {
    u = new URL(raw.trim());
  } catch {
    return null;
  }
  return u.protocol === "http:" || u.protocol === "https:" ? u : null;
}

/** Downloads the image with a hard 4 MiB cap, enforced while streaming (never after). */
async function fetchImageBytes(href) {
  let res;
  try {
    res = await fetch(href, { signal: AbortSignal.timeout(IMAGE_FETCH_TIMEOUT_MS), redirect: "follow" });
  } catch {
    throw new ImageFetchError("image fetch failed or timed out");
  }
  if (!res.ok) throw new ImageFetchError(`image fetch returned ${res.status}`);

  const declared = parseInt(res.headers.get("Content-Length") || "", 10);
  if (Number.isFinite(declared) && declared > MAX_IMAGE_BYTES) {
    throw new ImageFetchError("image exceeds the 4 MiB limit");
  }

  if (!res.body) {
    const buf = new Uint8Array(await res.arrayBuffer());
    if (buf.byteLength > MAX_IMAGE_BYTES) throw new ImageFetchError("image exceeds the 4 MiB limit");
    return buf;
  }

  const reader = res.body.getReader();
  const chunks = [];
  let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > MAX_IMAGE_BYTES) {
      try {
        await reader.cancel();
      } catch {
        // best-effort — the cap is already enforced
      }
      throw new ImageFetchError("image exceeds the 4 MiB limit");
    }
    chunks.push(value);
  }

  const out = new Uint8Array(total);
  let offset = 0;
  for (const c of chunks) {
    out.set(c, offset);
    offset += c.byteLength;
  }
  return out;
}

/**
 * Vision output varies by model family:
 *  - classifier models (e.g. @cf/microsoft/resnet-50) return [{label, score}, ...] -> top class + its score;
 *  - generative vision models return {response: string} -> the trimmed text is the label and
 *    confidence is 1 when a label was produced (no calibrated score exists — callers own thresholds).
 */
function imageVerdict(url, raw) {
  const arr = Array.isArray(raw) ? raw : raw && Array.isArray(raw.response) ? raw.response : null;
  if (arr && arr.length) {
    let top = null;
    for (const entry of arr) {
      if (!entry || typeof entry.label !== "string") continue;
      if (!top || (typeof entry.score === "number" && entry.score > (typeof top.score === "number" ? top.score : -1))) {
        top = entry;
      }
    }
    if (top) {
      return { url, label: top.label.slice(0, 200), confidence: clamp01(top.score) };
    }
  }

  const text = typeof raw === "string" ? raw : raw && typeof raw.response === "string" ? raw.response : null;
  if (text) {
    const label = text.trim().split("\n")[0].trim().slice(0, 200);
    if (label) return { url, label, confidence: 1 };
  }

  return { url, label: null, confidence: 0, reason: "empty model response" };
}

// ---------------------------------------------------------------------------
// Utils
// ---------------------------------------------------------------------------

/** Trimmed, deduplicated label list — or null if the input is unusable. */
function normalizeLabels(labels) {
  if (!Array.isArray(labels) || labels.length === 0 || labels.length > MAX_LABELS) return null;
  const out = [];
  for (const l of labels) {
    if (typeof l !== "string") return null;
    const t = l.trim();
    if (!t) return null;
    if (!out.includes(t)) out.push(t);
  }
  return out;
}

/** The caller's model id (validated) or the default; null means "reject the request". */
function chooseModel(requested, fallback) {
  if (requested == null) return fallback;
  if (typeof requested !== "string") return null;
  const m = requested.trim();
  if (!m) return fallback;
  if (m.length > 128) return null;
  if (!m.startsWith("@cf/") && !m.startsWith("@hf/")) return null;
  return m;
}

function clamp01(v) {
  const n = typeof v === "number" ? v : Number(v);
  if (!Number.isFinite(n)) return 0;
  return Math.min(1, Math.max(0, n));
}

/** Order-preserving concurrent map; `fn` is expected to catch its own errors. */
async function mapWithConcurrency(items, limit, fn) {
  const out = new Array(items.length);
  let next = 0;
  const lanes = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (true) {
      const i = next++;
      if (i >= items.length) return;
      out[i] = await fn(items[i], i);
    }
  });
  await Promise.all(lanes);
  return out;
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
    "Access-Control-Allow-Headers": "Authorization, Content-Type",
    "Access-Control-Max-Age": "86400",
  };
}

function json(body, status = 200) {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", ...corsHeaders() },
  });
}
