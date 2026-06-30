/**
 * Daleel log-viewer Worker
 * --------------------------------------------------------------------------
 * Permanent, browser-accessible (and admin-panel-callable) read-only view over
 * the `daleel-logs` R2 bucket, where Serilog writes Warning+ JSON-Lines logs.
 *
 * Endpoints (all require auth — see authorize()):
 *   GET /                      -> service banner + endpoint list (JSON)
 *   GET /logs                  -> list recent log objects (default last 7 days)
 *   GET /logs/{key}            -> read one log object (JSON-wrapped or ?format=raw)
 *   GET /logs/search?q=...     -> grep a pattern across recent logs
 *
 * Common query params:
 *   since=2h|30m|7d   relative lookback window (m=minutes, h=hours, d=days)
 *   limit=N           cap the number of objects considered / returned
 *
 * Auth: send `Authorization: Bearer <AUTH_TOKEN>` (server-to-server, e.g. the
 * Blazor admin panel) OR HTTP Basic auth with any username and the token as the
 * password (so a browser shows a native login prompt). AUTH_TOKEN is a Worker
 * secret: `wrangler secret put AUTH_TOKEN`.
 *
 * Designed for the Daleel admin panel: every response is JSON with a stable
 * shape, list/search return rich metadata, and CORS is honored so the endpoint
 * can be called from a browser if we ever move log viewing client-side.
 */

const DAY_MS = 24 * 60 * 60 * 1000;

// Hard ceilings so a single request can never fan out into an unbounded R2 read.
const MAX_LIST = 1000; // objects returned by /logs
const MAX_SEARCH_FILES = 200; // objects scanned by /logs/search
const MAX_MATCHES = 1000; // matches returned by /logs/search

export default {
  async fetch(request, env, ctx) {
    // Preflight: answer OPTIONS before auth so browsers can negotiate CORS.
    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: corsHeaders() });
    }

    const denied = authorize(request, env);
    if (denied) return denied;

    if (request.method !== "GET" && request.method !== "HEAD") {
      return json({ error: "method_not_allowed" }, 405);
    }

    const url = new URL(request.url);
    const path = decodeURIComponent(url.pathname);

    try {
      if (path === "/" || path === "") return root();
      if (path === "/logs" || path === "/logs/") return listLogs(url, env);
      if (path === "/logs/search") return searchLogs(url, env);
      if (path.startsWith("/logs/")) {
        const key = path.slice("/logs/".length);
        return readLog(key, url, env);
      }
      return json({ error: "not_found", path }, 404);
    } catch (err) {
      // Surface the message — this endpoint is for operators, not the public.
      return json({ error: "internal_error", message: String(err && err.message || err) }, 500);
    }
  },
};

// ---------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------

/** Returns a 401 Response if the request is not authorized, otherwise null. */
function authorize(request, env) {
  const expected = env.AUTH_TOKEN;
  if (!expected) {
    // Fail closed: a missing secret must never mean "open to the world".
    return json({ error: "server_misconfigured", message: "AUTH_TOKEN secret is not set" }, 500);
  }

  const header = request.headers.get("Authorization") || "";
  let presented = null;

  if (header.startsWith("Bearer ")) {
    presented = header.slice("Bearer ".length).trim();
  } else if (header.startsWith("Basic ")) {
    try {
      const decoded = atob(header.slice("Basic ".length).trim());
      // "user:password" — username is ignored, password carries the token.
      presented = decoded.slice(decoded.indexOf(":") + 1);
    } catch {
      presented = null;
    }
  }

  if (presented && timingSafeEqual(presented, expected)) return null;

  return new Response(
    JSON.stringify({ error: "unauthorized" }),
    {
      status: 401,
      headers: {
        "Content-Type": "application/json; charset=utf-8",
        // Prompt browsers for Basic creds; harmless for Bearer callers.
        "WWW-Authenticate": 'Basic realm="daleel-logs", charset="UTF-8"',
        ...corsHeaders(),
      },
    },
  );
}

/** Length-independent constant-time string compare. */
function timingSafeEqual(a, b) {
  const ab = new TextEncoder().encode(a);
  const bb = new TextEncoder().encode(b);
  // Compare against a fixed length so we never short-circuit on size.
  let diff = ab.length ^ bb.length;
  const len = Math.max(ab.length, bb.length);
  for (let i = 0; i < len; i++) {
    diff |= (ab[i] || 0) ^ (bb[i] || 0);
  }
  return diff === 0;
}

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

function root() {
  return json({
    service: "daleel-log-viewer",
    bucket: "daleel-logs",
    endpoints: {
      "GET /logs": "list recent log objects (params: since, limit, prefix)",
      "GET /logs/{key}": "read one log object (params: format=raw|json, tail=N)",
      "GET /logs/search": "search recent logs (params: q, since, limit, regex, ignoreCase)",
    },
  });
}

async function listLogs(url, env) {
  const since = parseSince(url.searchParams.get("since"), 7 * DAY_MS);
  const limit = clampInt(url.searchParams.get("limit"), MAX_LIST, MAX_LIST);
  const prefix = url.searchParams.get("prefix") || undefined;
  const cutoff = nowMs() - since;

  const objects = await listSince(env, cutoff, prefix, limit);
  const files = objects.map(describe).sort((a, b) => b.uploaded.localeCompare(a.uploaded));

  return json({
    bucket: "daleel-logs",
    since: msToSpan(since),
    count: files.length,
    files,
  });
}

async function readLog(key, url, env) {
  if (!key) return json({ error: "missing_key" }, 400);

  const obj = await env.LOGS_BUCKET.get(key);
  if (!obj) return json({ error: "not_found", key }, 404);

  const text = await obj.text();
  const format = (url.searchParams.get("format") || "json").toLowerCase();

  if (format === "raw") {
    return new Response(text, {
      status: 200,
      headers: {
        "Content-Type": "text/plain; charset=utf-8",
        ...corsHeaders(),
      },
    });
  }

  // Optional tail: return only the last N lines (handy for big files in the UI).
  let lines = text.length ? text.split(/\r?\n/) : [];
  // Drop a trailing empty line produced by a final newline.
  if (lines.length && lines[lines.length - 1] === "") lines.pop();
  const totalLines = lines.length;
  const tail = clampInt(url.searchParams.get("tail"), 0, 100000);
  if (tail > 0 && lines.length > tail) lines = lines.slice(-tail);

  return json({
    key,
    size: obj.size,
    uploaded: obj.uploaded.toISOString(),
    totalLines,
    returnedLines: lines.length,
    lines,
  });
}

async function searchLogs(url, env) {
  const q = url.searchParams.get("q");
  if (!q) return json({ error: "missing_query", message: "provide ?q=pattern" }, 400);

  const since = parseSince(url.searchParams.get("since"), DAY_MS);
  const fileLimit = clampInt(url.searchParams.get("limit"), MAX_SEARCH_FILES, MAX_SEARCH_FILES);
  const ignoreCase = url.searchParams.get("ignoreCase") !== "false";
  const useRegex = url.searchParams.get("regex") === "true";
  const cutoff = nowMs() - since;

  const matcher = buildMatcher(q, { useRegex, ignoreCase });
  if (matcher.error) return json({ error: "bad_pattern", message: matcher.error }, 400);

  const objects = await listSince(env, cutoff, undefined, fileLimit);
  // Newest first so the most relevant matches surface before we hit MAX_MATCHES.
  objects.sort((a, b) => b.uploaded - a.uploaded);

  const matches = [];
  let filesScanned = 0;
  let truncated = false;

  for (const meta of objects) {
    if (matches.length >= MAX_MATCHES) {
      truncated = true;
      break;
    }
    const obj = await env.LOGS_BUCKET.get(meta.key);
    if (!obj) continue;
    filesScanned++;
    const text = await obj.text();
    const lines = text.split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      if (!line) continue;
      if (matcher.test(line)) {
        matches.push({ key: meta.key, line: i + 1, text: line });
        if (matches.length >= MAX_MATCHES) {
          truncated = true;
          break;
        }
      }
    }
  }

  return json({
    query: q,
    regex: useRegex,
    ignoreCase,
    since: msToSpan(since),
    filesScanned,
    filesAvailable: objects.length,
    matchCount: matches.length,
    truncated,
    matches,
  });
}

// ---------------------------------------------------------------------------
// R2 helpers
// ---------------------------------------------------------------------------

/** List objects uploaded at/after `cutoff` (ms epoch), paging through R2. */
async function listSince(env, cutoff, prefix, limit) {
  const out = [];
  let cursor;
  do {
    const page = await env.LOGS_BUCKET.list({ prefix, cursor, limit: 1000 });
    for (const o of page.objects) {
      if (o.uploaded.getTime() >= cutoff) out.push(o);
    }
    cursor = page.truncated ? page.cursor : undefined;
    if (out.length >= limit) break;
  } while (cursor);
  return out.slice(0, limit);
}

function describe(o) {
  return {
    key: o.key,
    size: o.size,
    uploaded: o.uploaded.toISOString(),
    etag: o.etag,
  };
}

// ---------------------------------------------------------------------------
// Matching
// ---------------------------------------------------------------------------

function buildMatcher(q, { useRegex, ignoreCase }) {
  if (useRegex) {
    try {
      const re = new RegExp(q, ignoreCase ? "i" : "");
      return { test: (line) => re.test(line) };
    } catch (e) {
      return { error: `invalid regex: ${e.message}` };
    }
  }
  if (ignoreCase) {
    const needle = q.toLowerCase();
    return { test: (line) => line.toLowerCase().includes(needle) };
  }
  return { test: (line) => line.includes(q) };
}

// ---------------------------------------------------------------------------
// Time / parsing utils
// ---------------------------------------------------------------------------

function nowMs() {
  return Date.now();
}

/** Parse "2h" / "30m" / "7d" / raw-ms into milliseconds; fall back to default. */
function parseSince(raw, fallbackMs) {
  if (!raw) return fallbackMs;
  const m = /^(\d+)\s*([smhdw])?$/.exec(raw.trim());
  if (!m) return fallbackMs;
  const n = parseInt(m[1], 10);
  switch (m[2]) {
    case "s": return n * 1000;
    case "m": return n * 60 * 1000;
    case "h": return n * 60 * 60 * 1000;
    case "d": return n * DAY_MS;
    case "w": return n * 7 * DAY_MS;
    default: return n; // bare number = milliseconds
  }
}

function msToSpan(ms) {
  if (ms % DAY_MS === 0) return `${ms / DAY_MS}d`;
  if (ms % (60 * 60 * 1000) === 0) return `${ms / (60 * 60 * 1000)}h`;
  if (ms % (60 * 1000) === 0) return `${ms / (60 * 1000)}m`;
  return `${ms}ms`;
}

function clampInt(raw, fallback, max) {
  const n = parseInt(raw, 10);
  if (!Number.isFinite(n) || n < 0) return fallback;
  return Math.min(n, max);
}

// ---------------------------------------------------------------------------
// HTTP helpers
// ---------------------------------------------------------------------------

function corsHeaders() {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, HEAD, OPTIONS",
    "Access-Control-Allow-Headers": "Authorization, Content-Type",
    "Access-Control-Max-Age": "86400",
  };
}

function json(body, status = 200) {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      ...corsHeaders(),
    },
  });
}
