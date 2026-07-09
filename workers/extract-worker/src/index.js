/**
 * Daleel extract-worker
 * --------------------------------------------------------------------------
 * The STRUCTURED-EXTRACTION execution host of
 * docs/architecture/cloudflare-workers-pipeline.md (§3.3): raw HTML/markdown
 * in, Daleel's product JSON shape out, via Workers AI in JSON mode
 * (schema-forced `response_format`).
 *
 * v1 is deliberately SYNCHRONOUS and single-document — one page extract is a
 * matter of seconds, so there is nothing to queue yet. The doc's async batch
 * flow (202 + jobId, queue consumer, R2 EntityDocument writes, D1 edge-index
 * upserts, poll messages) is Phase 2 and slots in behind these same routes.
 *
 * Endpoints (all require auth — house pattern, same as scrape-worker):
 *   GET  /                    -> service banner + endpoint list (JSON)
 *   GET  /health              -> liveness
 *   POST /extract/products    -> sync: page content -> { products[], productCount }
 *                                in the VPS CatalogProduct shape (camelCase)
 *   POST /extract/structured  -> sync: caller supplies the JSON schema -> { data }
 *
 * Parser discipline: the model is prompted as a PARSER, never an author — it
 * extracts what is present, uses null for what isn't, and outputs only JSON.
 * JSON mode does the heavy lifting; a brace-scan fallback recovers the first
 * JSON object from raw text if the model ever answers with prose around it.
 */

/**
 * Default model (doc §3.3): best quality, 24k-token context. Longer inputs
 * need Scout (131k ctx) or chunk-and-merge — that tiering is Phase 2; v1
 * instead caps and trims the input to fit this model (see MAX_CONTENT_CHARS).
 */
const MODEL = "@cf/meta/llama-3.3-70b-instruct-fp8-fast";

/** Hard input cap — larger bodies get a 413. ~100 KB of text. */
const MAX_CONTENT_CHARS = 100_000;

/**
 * What actually goes to the model: ~24k tokens ≈ 90KB chars (≈4 chars/token)
 * for the 70B fp8-fast context window — hence the cap just above it. Content
 * between the two limits is trimmed (flagged via meta.truncated).
 */
const MODEL_INPUT_CHARS = 90_000;

/** Output budget for the extracted JSON (Workers AI default of 256 truncates). */
const MAX_OUTPUT_TOKENS = 4096;

/** The VPS CatalogProduct record, as a JSON schema (camelCase; VPS deserializes case-insensitively). */
const PRODUCTS_SCHEMA = {
  type: "object",
  properties: {
    products: {
      type: "array",
      items: {
        type: "object",
        properties: {
          name: { type: "string" },
          description: { type: ["string", "null"] },
          price: { type: ["number", "null"] },
          currency: { type: ["string", "null"] },
          url: { type: ["string", "null"] },
          category: { type: ["string", "null"] },
          imageUrl: { type: ["string", "null"] },
          sku: { type: ["string", "null"] },
        },
        required: ["name"],
      },
    },
  },
  required: ["products"],
};

const PARSER_SYSTEM_PROMPT = [
  "You are a strict structured-data extraction parser.",
  "You EXTRACT — you never invent, guess, infer beyond the text, or fill in missing values.",
  "A field that is not explicitly present in the input is null.",
  "Prices are plain numbers (no currency symbols); the currency goes in its own field.",
  "Output ONLY JSON that matches the requested schema — no prose, no markdown fences, no commentary.",
].join(" ");

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
        if (path === "/" || path === "") return root(env);
        if (path === "/health") return json({ ok: true });
        return json({ ok: false, error: err("not_found", `no route ${path}`, false) }, 404);
      }

      if (request.method === "POST") {
        const body = await readJson(request);
        if (body.error) return json({ ok: false, error: err("bad_request", body.error, false) }, 400);

        if (path === "/extract/products") return extractProducts(body.value, env);
        if (path === "/extract/structured") return extractStructured(body.value, env);
        return json({ ok: false, error: err("not_found", `no route ${path}`, false) }, 404);
      }

      return json({ ok: false, error: err("method_not_allowed", request.method, false) }, 405);
    } catch (e) {
      // Details go to the worker logs (observability), never to the response: even though every
      // caller is our own authenticated VPS, error internals don't belong on the wire.
      console.error("extract-worker unhandled error:", e);
      return json({ ok: false, error: err("internal_error", "unexpected failure — see worker logs", true) }, 500);
    }
  },
};

// ---------------------------------------------------------------------------
// Auth (house pattern — identical to workers/scrape-worker and workers/log-viewer)
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
      "WWW-Authenticate": 'Basic realm="daleel-extract", charset="UTF-8"',
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

function root(env) {
  return json({
    service: "daleel-extract-worker",
    role: "structured-extraction execution host (v1: synchronous single-document, Workers AI JSON mode)",
    environment: env.ENVIRONMENT || "dev",
    model: MODEL,
    endpoints: {
      "GET /health": "liveness",
      "POST /extract/products": "sync page text/markdown -> product JSON { content, market?, intent? }",
      "POST /extract/structured": "sync caller-schema extraction { content, schema }",
    },
  });
}

/** POST /extract/products — page content in, CatalogProduct-shaped products out. */
async function extractProducts(body, env) {
  const checked = validateContent(body);
  if (checked.deny) return checked.deny;
  const { content, truncated } = checked;

  const market = optionalString(body, "market");
  const intent = optionalString(body, "intent");

  const started = Date.now();
  const outcome = await runExtraction(env, {
    schema: PRODUCTS_SCHEMA,
    userPrompt: [
      "Extract every distinct product from the page content below.",
      market ? `Target market: ${market} (a locale/currency disambiguation hint — never a reason to invent values).` : null,
      intent ? `Search intent (a relevance hint only): ${intent}` : null,
      'Return JSON of the shape {"products":[{"name","description","price","currency","url","category","imageUrl","sku"}]}.',
      "Every field except name is null when the content does not state it. No products means an empty products array.",
      "",
      "PAGE CONTENT:",
      content,
    ]
      .filter((line) => line !== null)
      .join("\n"),
  });
  if (outcome.deny) return outcome.deny;

  const rawList = Array.isArray(outcome.parsed && outcome.parsed.products)
    ? outcome.parsed.products
    : Array.isArray(outcome.parsed)
      ? outcome.parsed // lenient: some models emit the bare array despite the schema
      : null;
  if (!rawList) {
    console.error("extract-worker: model JSON lacked a products array:", JSON.stringify(outcome.parsed).slice(0, 1000));
    return json({ ok: false, error: err("extraction_unparseable", "model output was not the requested shape", true) }, 502);
  }

  const products = rawList.map(normalizeProduct).filter(Boolean);
  return json({
    ok: true,
    mode: "sync",
    model: MODEL,
    result: { products, productCount: products.length },
    meta: { ms: Date.now() - started, truncated },
  });
}

/** POST /extract/structured — same flow, but the caller supplies the JSON schema. */
async function extractStructured(body, env) {
  const checked = validateContent(body);
  if (checked.deny) return checked.deny;
  const { content, truncated } = checked;

  const schema = body.schema;
  if (!schema || typeof schema !== "object" || Array.isArray(schema)) {
    return json({ ok: false, error: err("bad_request", "schema (a JSON Schema object) is required", false) }, 400);
  }

  const started = Date.now();
  const outcome = await runExtraction(env, {
    schema,
    userPrompt: [
      "Extract the data described by the requested JSON schema from the content below.",
      "Fields the content does not state are null; do not fabricate anything.",
      "",
      "CONTENT:",
      content,
    ].join("\n"),
  });
  if (outcome.deny) return outcome.deny;

  return json({
    ok: true,
    mode: "sync",
    model: MODEL,
    result: { data: outcome.parsed },
    meta: { ms: Date.now() - started, truncated },
  });
}

// ---------------------------------------------------------------------------
// Workers AI call + JSON recovery
// ---------------------------------------------------------------------------

/**
 * One schema-forced Workers AI call. Returns { parsed } on success or
 * { deny: Response } on failure — AI transport errors and unparseable output
 * are both surfaced as retryable 502 envelopes (details to logs only).
 */
async function runExtraction(env, { schema, userPrompt }) {
  let res;
  try {
    res = await env.AI.run(MODEL, {
      messages: [
        { role: "system", content: PARSER_SYSTEM_PROMPT },
        { role: "user", content: userPrompt },
      ],
      // JSON mode: the runtime constrains decoding to the schema. The raw-text
      // fallback below still guards against providers/models that regress to text.
      response_format: { type: "json_schema", json_schema: schema },
      max_tokens: MAX_OUTPUT_TOKENS,
      temperature: 0,
    });
  } catch (e) {
    console.error("extract-worker Workers AI call failed:", e);
    return { deny: json({ ok: false, error: err("ai_unavailable", "Workers AI call failed — see worker logs", true) }, 502) };
  }

  const parsed = parseModelJson(res);
  if (parsed === undefined) {
    console.error("extract-worker unparseable model output:", JSON.stringify(res).slice(0, 2000));
    return { deny: json({ ok: false, error: err("extraction_unparseable", "model output was not valid JSON", true) }, 502) };
  }
  return { parsed };
}

/**
 * Workers AI JSON-mode responses arrive as { response: <object> } when the
 * constraint held, or { response: "<string>" } otherwise. Take the object,
 * else JSON.parse the string, else salvage the first JSON object from raw
 * text (models love ```json fences and stray prose).
 */
function parseModelJson(res) {
  const raw = res && typeof res === "object" && !Array.isArray(res) && "response" in res ? res.response : res;
  if (raw && typeof raw === "object") return raw;
  if (typeof raw === "string" && raw.length) {
    try {
      return JSON.parse(raw);
    } catch {
      return firstJsonObject(raw);
    }
  }
  return undefined;
}

/**
 * First balanced {...} in the text, string/escape aware. A deliberate
 * character-walk — no regex over model output (house rule: nothing
 * polynomially backtrackable on external input).
 */
function firstJsonObject(text) {
  const start = text.indexOf("{");
  if (start === -1) return undefined;
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let i = start; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      if (escaped) escaped = false;
      else if (c === "\\") escaped = true;
      else if (c === '"') inString = false;
      continue;
    }
    if (c === '"') inString = true;
    else if (c === "{") depth++;
    else if (c === "}") {
      depth--;
      if (depth === 0) {
        try {
          return JSON.parse(text.slice(start, i + 1));
        } catch {
          return undefined;
        }
      }
    }
  }
  return undefined;
}

// ---------------------------------------------------------------------------
// Validation & product normalization
// ---------------------------------------------------------------------------

/**
 * Shared content check: required non-empty string, 413 over the hard cap,
 * trimmed to the model's context (flagged) between the two limits.
 * Returns { deny: Response } or { content, truncated }.
 */
function validateContent(body) {
  if (!body || typeof body.content !== "string" || !body.content.trim()) {
    return { deny: json({ ok: false, error: err("bad_request", "content (page text/markdown) is required", false) }, 400) };
  }
  if (body.content.length > MAX_CONTENT_CHARS) {
    return {
      deny: json(
        {
          ok: false,
          error: err(
            "payload_too_large",
            `content exceeds ${MAX_CONTENT_CHARS} chars (~100 KB); split the document (the Phase-2 async batch path will lift this)`,
            false,
          ),
        },
        413,
      ),
    };
  }
  const truncated = body.content.length > MODEL_INPUT_CHARS;
  return { content: truncated ? body.content.slice(0, MODEL_INPUT_CHARS) : body.content, truncated };
}

/**
 * One CatalogProduct in the exact camelCase shape the VPS deserializes
 * (case-insensitive) — same contract as scrape-worker's catalogue results.
 */
function normalizeProduct(p) {
  if (!p || typeof p !== "object") return null;
  const name = firstString(p, "name");
  if (!name) return null;
  return {
    name,
    description: firstString(p, "description"),
    price: numberOrNull(p.price),
    currency: firstString(p, "currency"),
    url: firstString(p, "url"),
    category: firstString(p, "category"),
    imageUrl: firstString(p, "imageUrl", "image_url"),
    sku: firstString(p, "sku"),
  };
}

/** Finite number, or a numeric string coerced ("12.99" — models slip these in); else null. */
function numberOrNull(v) {
  if (typeof v === "number" && Number.isFinite(v)) return v;
  if (typeof v === "string" && v.trim()) {
    const n = Number(v.trim());
    if (Number.isFinite(n)) return n;
  }
  return null;
}

// ---------------------------------------------------------------------------
// Utils (house pattern)
// ---------------------------------------------------------------------------

function optionalString(obj, key) {
  const v = obj[key];
  return typeof v === "string" && v.trim() ? v.trim() : null;
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
