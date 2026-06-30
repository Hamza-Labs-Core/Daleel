# daleel-log-viewer (Cloudflare Worker)

Permanent, browser- and admin-panel-accessible **read-only** HTTP view over the
`daleel-logs` R2 bucket (where Serilog writes Warning+ JSON-Lines logs). No more
`docker logs` over SSH to read what already landed in R2.

## Endpoints

All endpoints require auth and return JSON (except `/logs/{key}?format=raw`).

| Method & path | Purpose |
| --- | --- |
| `GET /` | Service banner + endpoint list |
| `GET /logs` | List recent log objects. Params: `since` (default `7d`), `limit`, `prefix` |
| `GET /logs/{key}` | Read one log object. Params: `format=json\|raw` (default `json`), `tail=N` (last N lines) |
| `GET /logs/search` | Grep recent logs. Params: `q` (required), `since` (default `1d`), `limit`, `regex=true\|false`, `ignoreCase=true\|false` |

`since` accepts `30m`, `2h`, `7d`, `1w` (s/m/h/d/w), or a bare number of milliseconds.

### Examples

```bash
BASE=https://daleel-log-viewer.<your-subdomain>.workers.dev
TOKEN=...   # the AUTH_TOKEN secret

curl -H "Authorization: Bearer $TOKEN" "$BASE/logs?since=2d"
curl -H "Authorization: Bearer $TOKEN" "$BASE/logs/2026-06-29.jsonl?tail=100"
curl -H "Authorization: Bearer $TOKEN" "$BASE/logs/search?q=error&since=2h"
```

In a browser you'll get a Basic-auth prompt: leave the username blank (or type
anything) and paste the token as the password.

## Auth

Send `Authorization: Bearer <AUTH_TOKEN>` (server-to-server) **or** HTTP Basic
auth with the token as the password (browser-friendly). `AUTH_TOKEN` is a Worker
secret — if it isn't set the Worker fails closed (500), never open.

## Deploy

```bash
cd workers/log-viewer
npm install                 # optional; wrangler can run via npx
npx wrangler login          # one-time, unless CLOUDFLARE_API_TOKEN is set
npx wrangler secret put AUTH_TOKEN   # paste a long random string
npx wrangler deploy
```

Deploying needs a Cloudflare API token / login with **Workers Scripts: Edit**
and **Workers R2 Storage** permissions. This is a *different* credential from the
S3-style `R2_ACCESS_KEY`/`R2_SECRET_KEY` the .NET app uses — those cannot deploy
Workers.

Generate a token: `openssl rand -hex 32`.

## Local dev

```bash
cp .dev.vars.example .dev.vars   # set AUTH_TOKEN
npx wrangler dev                 # binds the real daleel-logs bucket by default
```

## Calling it from the Daleel admin panel

The admin panel (Blazor Server) can call this server-side with `HttpClient`:

```csharp
var http = new HttpClient { BaseAddress = new Uri(logViewerBaseUrl) };
http.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", logViewerToken);

// list recent files
var files = await http.GetFromJsonAsync<JsonElement>("/logs?since=7d");
// search
var hits  = await http.GetFromJsonAsync<JsonElement>("/logs/search?q=error&since=2h");
```

Suggested config keys (mirroring the existing R2 env-var style):
`LOG_VIEWER_URL` and `LOG_VIEWER_TOKEN`. Responses have a stable JSON shape, so
the panel can render a file list, a tail view, and a search box directly.
