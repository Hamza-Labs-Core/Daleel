/**
 * Daleel filter-worker
 * --------------------------------------------------------------------------
 * The HALAL-SIGNAL execution host of docs/architecture/cloudflare-workers-pipeline.md
 * (§3.4): the LLM and vision *classification layers* of `HalalModerator`
 * (`LlmHalalClassifier`, `OpenRouterImageHalalClassifier`) relocated to Workers AI.
 *
 * ==========================================================================
 * CRITICAL FRAMING — THIS WORKER RETURNS FINDINGS ONLY. IT NEVER DECIDES.
 * ==========================================================================
 * The VPS `HalalModerator` keeps ALL policy: the whitelist layer, the keyword
 * layers, per-category thresholds, the riba-never-filtered rule, the
 * SHOW-BY-DEFAULT >= 0.8 bar, haram-wins dedupe, image-strip-vs-removal,
 * admin veto, and audit logging. This worker is a stateless classifier the
 * VPS calls — raw signals in, raw findings out. A worker error (or timeout,
 * or unparseable model output) means "no finding": the VPS fails open and
 * shows the content. Nothing in this file may remove, hide, or threshold
 * anything.
 *
 * Its output feeds an A/B against the current OpenRouter classifier on a
 * labeled set BEFORE any default flips (doc §6 Phase 3) — moderation
 * precision is a tracked metric.
 *
 * Endpoints (all require auth — house pattern, same as scrape-worker):
 *   GET  /               -> service banner + endpoint list (JSON)
 *   GET  /health         -> liveness
 *   POST /filter/text    -> sync: { items:[{id,text,sourceUrl?}] } -> two
 *                           best-effort signals per item:
 *                             (a) @cf/meta/llama-guard-3-8b   source "llama-guard"
 *                                 (GENERIC safety categories, not halal-specific)
 *                             (b) @cf/meta/llama-3.1-8b-instruct with the halal
 *                                 screening prompt, JSON mode   source "llm"
 *   POST /filter/images  -> sync: { urls:[...] } -> per-url vision screening
 *                           via @cf/meta/llama-3.2-11b-vision-instruct,
 *                           source "vision" (http(s) only, <= 4 MiB per image)
 *
 * Items/urls with no finding are simply absent from `findings` — absence is
 * the "clean" verdict, and a failed signal is indistinguishable from clean
 * by design (fail-open). Per-signal failure counts are surfaced in `meta`
 * so the VPS/A-B harness can tell "considered and clean" batches from
 * "signal never ran" batches in aggregate.
 *
 * RIBA POLICY (mirrors src/Daleel.Agent/PromptTemplates.cs HalalGuard):
 * we filter haram CONTENT (what a result sells/shows), never a store's
 * financing model. Banks and retailers offering interest-based (riba)
 * installment plans MUST still appear — the user can pay cash. So riba /
 * interest / banking is NOT a category here: the prompts forbid it AND the
 * category allow-list below cannot express it. Never emit a finding for it.
 */

// Models — overridable via [vars] for A/B iteration without a code change.
const DEFAULT_GUARD_MODEL = "@cf/meta/llama-guard-3-8b";
const DEFAULT_TEXT_MODEL = "@cf/meta/llama-3.1-8b-instruct";
const DEFAULT_VISION_MODEL = "@cf/meta/llama-3.2-11b-vision-instruct";

/** Request caps (cost guards — the VPS batches beneath these). */
const MAX_TEXT_ITEMS = 50;
const MAX_IMAGE_URLS = 20;

/** Per-item text ceiling sent to the models (titles/snippets are short; this bounds cost). */
const MAX_TEXT_CHARS = 6000;

/** Image fetch guard (same as classify-worker): http(s) only, hard byte cap. */
const MAX_IMAGE_BYTES = 4 * 1024 * 1024; // 4 MiB
const IMAGE_FETCH_TIMEOUT_MS = 20_000;

/** How many parallel model calls per request (latency vs. subrequest budget). */
const GUARD_CONCURRENCY = 5;
const VISION_CONCURRENCY = 3;

/** Items per llama-3.1 halal-screening call (mirrors the VPS LlmHalalClassifier batching). */
const LLM_CHUNK_SIZE = 10;

/**
 * Llama Guard emits a binary safe/unsafe verdict with NO calibrated confidence. 0.5 is a
 * deliberate nominal value: it sits below every VPS removal threshold (default 0.8, learned
 * floor 0.65), so under current policy this signal can never remove an item on its own —
 * it exists as corroboration and as an A/B datapoint. The VPS owns what to make of it.
 */
const GUARD_NOMINAL_CONFIDENCE = 0.5;

/**
 * The ONLY categories a finding may carry — mirrors HalalPolicy.AllowedCategories
 * (src/Daleel.Core/Moderation/HalalClassification.cs). Anything else the model says is
 * dropped here (the VPS would discard it anyway; dropping keeps the A/B clean).
 * Note what is ABSENT: riba/interest/banking — see the riba policy note above.
 */
const ALLOWED_CATEGORIES = new Set(["alcohol", "pork", "gambling", "adult", "drugs", "tobacco", "immodest"]);

/**
 * Belt-and-suspenders mirror of HalalPolicy.NeverFiltered: even if a model ignores both the
 * prompt and the schema enum, a finding in these categories is dropped before it leaves this
 * worker. Riba is never filtered — that invariant must not depend on model obedience.
 */
const NEVER_A_FINDING = new Set([
  "riba", "interest", "banking", "bank", "finance", "financial", "insurance", "loans", "mortgage",
]);

/** Llama Guard 3 hazard codes -> readable generic-safety names (NOT halal categories). */
const GUARD_CATEGORY_NAMES = {
  S1: "violent_crimes",
  S2: "non_violent_crimes",
  S3: "sex_crimes",
  S4: "child_exploitation",
  S5: "defamation",
  S6: "specialized_advice",
  S7: "privacy",
  S8: "intellectual_property",
  S9: "indiscriminate_weapons",
  S10: "hate",
  S11: "self_harm",
  S12: "sexual_content",
  S13: "elections",
  S14: "code_interpreter_abuse",
};

/**
 * Halal screening prompt for the instruct model. Categories mirror PromptTemplates.HalalGuard;
 * the riba exclusion is stated in the model's face AND enforced by the allow-list above.
 * "When in doubt, report nothing" mirrors the platform's show-by-default bias — but the
 * threshold decision itself still happens on the VPS, not here.
 */
const HALAL_TEXT_SYSTEM_PROMPT =
  "You screen e-commerce listing text for a halal marketplace. For each item, decide whether the " +
  "text itself promotes or sells any of EXACTLY these categories: " +
  '"alcohol" (incl. wine, beer, spirits), "pork" (or other non-halal meat), "gambling", ' +
  '"adult" (pornographic/sexual content), "immodest" (immodest apparel/content), ' +
  '"drugs" (narcotics/recreational), "tobacco" (incl. vaping). ' +
  "IMPORTANT PLATFORM POLICY: do NOT flag a store or product merely because it is a bank or offers " +
  "interest-based (riba) financing or installment plans — the user can pay cash, so such results are " +
  "allowed. Interest-based finance is NOT a category and you must NEVER report a finding for it. " +
  "Only report items that CLEARLY violate a category; when in doubt, report nothing for that item. " +
  'Respond with JSON only: {"verdicts":[{"id":"<item id>","category":"<one category>",' +
  '"confidence":<0..1>,"reason":"<short reason>"}]} — list ONLY violating items; an empty ' +
  '"verdicts" array means every item is clean.';

const HALAL_VISION_PROMPT =
  "You screen a product/store image for a halal marketplace. Decide whether the image CLEARLY shows " +
  "or promotes one of EXACTLY these categories: " +
  '"alcohol", "pork" (or other non-halal meat), "gambling", "adult", "immodest", "drugs", "tobacco". ' +
  "Banks, financing offers, and interest-based (riba) services are NOT categories — never flag them. " +
  'If the image clearly violates a category, respond {"category":"<category>","confidence":<0..1>,' +
  '"reason":"<short reason>"}. Otherwise respond {"category":null,"confidence":0,"reason":""}. ' +
  "Respond with JSON only, no prose.";

/** JSON-mode schema for the batched halal text screening call. */
const HALAL_VERDICTS_SCHEMA = {
  type: "object",
  properties: {
    verdicts: {
      type: "array",
      items: {
        type: "object",
        properties: {
          id: { type: "string" },
          category: { type: "string", enum: [...ALLOWED_CATEGORIES] },
          confidence: { type: "number" },
          reason: { type: "string" },
        },
        required: ["id", "category", "confidence"],
      },
    },
  },
  required: ["verdicts"],
};

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

        if (path === "/filter/text") return filterText(body.value, env);
        if (path === "/filter/images") return filterImages(body.value, env);
        return json({ ok: false, error: err("not_found", `no route ${path}`, false) }, 404);
      }

      return json({ ok: false, error: err("method_not_allowed", request.method, false) }, 405);
    } catch (e) {
      // Details go to the worker logs (observability), never to the response: even though every
      // caller is our own authenticated VPS, error internals don't belong on the wire.
      console.error("filter-worker unhandled error:", e);
      return json({ ok: false, error: err("internal_error", "unexpected failure — see worker logs", true) }, 500);
    }
  },
};

// ---------------------------------------------------------------------------
// Auth (house pattern — identical to workers/scrape-worker / log-viewer)
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

  return new Response(JSON.stringify({ ok: false, error: err("unauthorized", "bad or missing token", false) }), {
    status: 401,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "WWW-Authenticate": 'Basic realm="daleel-filter", charset="UTF-8"',
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
    service: "daleel-filter-worker",
    role: "halal-signal execution host (§3.4) — FINDINGS ONLY; all policy (whitelist, thresholds, riba-never-filtered, show-by-default, dedupe, veto, audit) stays on the VPS HalalModerator",
    endpoints: {
      "GET /health": "liveness",
      "POST /filter/text": "sync halal text signals { items: [{id, text, sourceUrl?}] } (max 50) -> { findings: [{id, category, confidence, reason, source}] }",
      "POST /filter/images": "sync halal image signals { urls: [string] } (max 20, http(s) only, <= 4 MiB each) -> { findings: [{url, category, confidence, reason, source}] }",
    },
  });
}

/**
 * POST /filter/text — two best-effort signals per item. A failed signal contributes
 * NO finding (fail-open); the VPS must treat absence as "nothing to report".
 * An optional `policy` field in the body is accepted for forward-compat with the doc's
 * FilterTextRequest but deliberately IGNORED — thresholds are applied on the VPS only.
 */
async function filterText(body, env) {
  const items = validateTextItems(body);
  if (items.error) return json({ ok: false, error: err("bad_request", items.error, false) }, 400);

  const started = Date.now();
  const failures = { "llama-guard": 0, llm: 0 };

  // Both signals run concurrently; each is independently best-effort.
  const [guardFindings, llmFindings] = await Promise.all([
    runLlamaGuardSignal(items.value, env, failures),
    runHalalLlmSignal(items.value, env, failures),
  ]);

  const findings = [...guardFindings, ...llmFindings];
  return json({
    ok: true,
    mode: "sync",
    result: { findings },
    // signalFailures counts items whose model call errored — those items got NO finding
    // (fail-open), which is invisible in `findings` itself but matters to the A/B harness.
    meta: { ms: Date.now() - started, items: items.value.length, signalFailures: failures },
  });
}

/**
 * POST /filter/images — vision screening per URL. Unfetchable, non-http(s), oversized,
 * or unparseable images simply produce no finding (fail-open), never an error response.
 */
async function filterImages(body, env) {
  if (!body || !Array.isArray(body.urls)) {
    return json({ ok: false, error: err("bad_request", "urls must be an array of strings", false) }, 400);
  }
  if (body.urls.length > MAX_IMAGE_URLS) {
    return json({ ok: false, error: err("bad_request", `too many urls (max ${MAX_IMAGE_URLS})`, false) }, 400);
  }
  for (let i = 0; i < body.urls.length; i++) {
    if (typeof body.urls[i] !== "string" || !body.urls[i].trim()) {
      return json({ ok: false, error: err("bad_request", `urls[${i}] must be a non-empty string`, false) }, 400);
    }
  }

  const started = Date.now();
  const failures = { vision: 0 };

  const results = await mapPool(body.urls, VISION_CONCURRENCY, async (url) => {
    try {
      return await screenImage(url.trim(), env);
    } catch (e) {
      // Fail-open: an errored image is "no finding", never a blocked pipeline.
      console.error("filter-worker vision signal failed for url:", url, e);
      failures.vision++;
      return null;
    }
  });

  const findings = results.filter(Boolean);
  return json({
    ok: true,
    mode: "sync",
    result: { findings },
    meta: { ms: Date.now() - started, urls: body.urls.length, signalFailures: failures },
  });
}

// ---------------------------------------------------------------------------
// Signal (a): Llama Guard — GENERIC safety classification, per item
// ---------------------------------------------------------------------------

/**
 * Runs @cf/meta/llama-guard-3-8b over each item. Its categories are generic safety
 * hazards (S1–S14), NOT halal categories — the finding is labeled source "llama-guard"
 * so the VPS/A-B harness can weigh it as corroboration, never as a halal verdict.
 */
async function runLlamaGuardSignal(items, env, failures) {
  const model = env.TEXT_GUARD_MODEL || DEFAULT_GUARD_MODEL;
  const results = await mapPool(items, GUARD_CONCURRENCY, async (item) => {
    try {
      const raw = await env.AI.run(model, {
        messages: [{ role: "user", content: item.text.slice(0, MAX_TEXT_CHARS) }],
      });
      const parsed = parseGuardResponse(raw);
      if (!parsed || parsed.safe !== false) return null; // clean (or unparseable → no finding)

      const codes = parsed.categories;
      const category = codes.length ? GUARD_CATEGORY_NAMES[codes[0]] || codes[0].toLowerCase() : "unsafe";
      if (NEVER_A_FINDING.has(category)) return null; // riba-shaped categories never leave this worker
      return {
        id: item.id,
        category,
        confidence: GUARD_NOMINAL_CONFIDENCE,
        reason: `Llama Guard 3: unsafe (${codes.join(", ") || "no hazard code"}) — generic safety signal, not halal-specific`,
        source: "llama-guard",
      };
    } catch (e) {
      // Fail-open: this item gets no llama-guard finding.
      console.error("filter-worker llama-guard signal failed for item:", item.id, e);
      failures["llama-guard"]++;
      return null;
    }
  });
  return results.filter(Boolean);
}

/** Accepts both Workers AI response shapes: { safe, categories } object or raw "unsafe\nS2" text. */
function parseGuardResponse(raw) {
  let r = raw && typeof raw === "object" && "response" in raw ? raw.response : raw;
  if (r && typeof r === "object" && typeof r.safe === "boolean") {
    return { safe: r.safe, categories: Array.isArray(r.categories) ? r.categories.map((c) => String(c).toUpperCase()) : [] };
  }
  if (typeof r === "string") {
    // Single character-class split — linear, no backtracking.
    const tokens = r.trim().split(/[\s,]+/);
    const verdict = (tokens[0] || "").toLowerCase();
    if (verdict === "safe") return { safe: true, categories: [] };
    if (verdict === "unsafe") {
      const categories = tokens
        .slice(1)
        .map((t) => t.toUpperCase())
        .filter((t) => t.startsWith("S"));
      return { safe: false, categories };
    }
  }
  return null;
}

// ---------------------------------------------------------------------------
// Signal (b): instruct model with the halal screening prompt, JSON mode
// ---------------------------------------------------------------------------

/**
 * Runs the halal-specific screening prompt over the items in chunks (mirrors the VPS
 * LlmHalalClassifier's batching). Each chunk is independently best-effort: a failed
 * chunk yields no findings for its items (fail-open), never an error response.
 */
async function runHalalLlmSignal(items, env, failures) {
  const model = env.TEXT_LLM_MODEL || DEFAULT_TEXT_MODEL;
  const chunks = [];
  for (let i = 0; i < items.length; i += LLM_CHUNK_SIZE) chunks.push(items.slice(i, i + LLM_CHUNK_SIZE));

  const perChunk = await mapPool(chunks, 2, async (chunk) => {
    try {
      return await halalLlmChunk(chunk, env, model);
    } catch (e) {
      console.error("filter-worker llm signal failed for chunk of", chunk.length, "items:", e);
      failures.llm += chunk.length;
      return [];
    }
  });
  return perChunk.flat();
}

async function halalLlmChunk(chunk, env, model) {
  // Ids go to the model as strings; byId maps them back to the caller's original values.
  const byId = new Map(chunk.map((it) => [String(it.id), it]));
  const payload = chunk.map((it) => ({ id: String(it.id), text: it.text.slice(0, MAX_TEXT_CHARS) }));

  const raw = await env.AI.run(model, {
    messages: [
      { role: "system", content: HALAL_TEXT_SYSTEM_PROMPT },
      { role: "user", content: `Classify these listings:\n${JSON.stringify(payload)}` },
    ],
    response_format: { type: "json_schema", json_schema: HALAL_VERDICTS_SCHEMA },
    max_tokens: 900,
  });

  const out = coerceJson(raw);
  const verdicts = out && Array.isArray(out.verdicts) ? out.verdicts : [];

  const findings = [];
  for (const v of verdicts) {
    if (!v || typeof v !== "object") continue;
    const item = byId.get(String(v.id));
    if (!item) continue; // hallucinated id
    const category = typeof v.category === "string" ? v.category.trim().toLowerCase() : "";
    if (NEVER_A_FINDING.has(category)) continue; // riba is NEVER a finding — hard invariant
    if (!ALLOWED_CATEGORIES.has(category)) continue; // off-list categories are dropped
    findings.push({
      id: item.id,
      category,
      confidence: clamp01(v.confidence),
      reason: typeof v.reason === "string" ? v.reason.slice(0, 500) : null,
      source: "llm",
    });
  }
  return findings;
}

// ---------------------------------------------------------------------------
// Vision signal: image screening
// ---------------------------------------------------------------------------

/** Screens one image URL; returns a finding or null. Throws only for the caller's fail-open catch. */
async function screenImage(url, env) {
  const bytes = await fetchImageGuarded(url);
  if (!bytes) return null; // non-http(s) / unfetchable / oversized → no finding (fail-open)

  const model = env.VISION_MODEL || DEFAULT_VISION_MODEL;
  const raw = await env.AI.run(model, {
    prompt: HALAL_VISION_PROMPT,
    image: [...bytes],
    max_tokens: 256,
  });

  const out = coerceJson(raw);
  if (!out || typeof out !== "object") return null;
  const category = typeof out.category === "string" ? out.category.trim().toLowerCase() : "";
  if (NEVER_A_FINDING.has(category)) return null; // riba is NEVER a finding — hard invariant
  if (!ALLOWED_CATEGORIES.has(category)) return null; // includes the explicit-null clean verdict

  return {
    url,
    category,
    confidence: clamp01(out.confidence),
    reason: typeof out.reason === "string" ? out.reason.slice(0, 500) : null,
    source: "vision",
  };
}

/**
 * Image fetch guard (same as classify-worker): http(s) URLs only, response streamed with a
 * hard 4 MiB cap (Content-Length alone is advisory — the stream is counted too). Returns
 * the bytes or null; never throws for guard reasons.
 */
async function fetchImageGuarded(url) {
  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return null;

  let res;
  try {
    res = await fetch(parsed.toString(), {
      headers: { Accept: "image/*" },
      signal: AbortSignal.timeout(IMAGE_FETCH_TIMEOUT_MS),
    });
  } catch (e) {
    console.error("filter-worker image fetch failed:", url, e);
    return null;
  }
  if (!res.ok || !res.body) return null;

  const declared = parseInt(res.headers.get("Content-Length") || "", 10);
  if (Number.isFinite(declared) && declared > MAX_IMAGE_BYTES) {
    try {
      await res.body.cancel();
    } catch {}
    return null;
  }

  const reader = res.body.getReader();
  const parts = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > MAX_IMAGE_BYTES) {
      try {
        await reader.cancel();
      } catch {}
      return null;
    }
    parts.push(value);
  }

  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const p of parts) {
    bytes.set(p, offset);
    offset += p.byteLength;
  }
  return bytes;
}

// ---------------------------------------------------------------------------
// Validation & utils
// ---------------------------------------------------------------------------

/** Validates the /filter/text body; returns { value } (normalized items) or { error }. */
function validateTextItems(body) {
  if (!body || !Array.isArray(body.items)) return { error: "items must be an array" };
  if (body.items.length > MAX_TEXT_ITEMS) return { error: `too many items (max ${MAX_TEXT_ITEMS})` };

  const items = [];
  for (let i = 0; i < body.items.length; i++) {
    const it = body.items[i];
    if (!it || typeof it !== "object") return { error: `items[${i}] must be an object` };
    if (it.id === null || it.id === undefined || it.id === "") return { error: `items[${i}].id is required` };
    if (typeof it.text !== "string" || !it.text.trim()) return { error: `items[${i}].text must be a non-empty string` };
    items.push({
      id: it.id,
      text: it.text.trim(),
      sourceUrl: typeof it.sourceUrl === "string" ? it.sourceUrl : null,
    });
  }
  return { value: items };
}

/**
 * Coerces a Workers AI response into a parsed JSON object: unwraps { response },
 * passes objects through, and extracts the first {...} block from prose-wrapped text
 * (index scan, no regex). Returns null when nothing parseable is found.
 */
function coerceJson(raw) {
  let r = raw && typeof raw === "object" && "response" in raw ? raw.response : raw;
  if (r && typeof r === "object") return r;
  if (typeof r !== "string") return null;
  const start = r.indexOf("{");
  const end = r.lastIndexOf("}");
  if (start === -1 || end <= start) return null;
  try {
    return JSON.parse(r.slice(start, end + 1));
  } catch {
    return null;
  }
}

function clamp01(v) {
  const n = typeof v === "number" ? v : parseFloat(v);
  if (!Number.isFinite(n)) return 0;
  return Math.min(1, Math.max(0, n));
}

/** Maps over items with at most `limit` concurrent invocations; preserves order. */
async function mapPool(items, limit, fn) {
  const results = new Array(items.length);
  let next = 0;
  async function lane() {
    while (next < items.length) {
      const i = next++;
      results[i] = await fn(items[i], i);
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, lane));
  return results;
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
