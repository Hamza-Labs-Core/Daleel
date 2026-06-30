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

### CI/CD (automatic)

Pushing changes under `workers/log-viewer/**` to `main` triggers
[`.github/workflows/deploy-worker.yml`](../../.github/workflows/deploy-worker.yml),
which runs `wrangler deploy` and (re)uploads the `AUTH_TOKEN` Worker secret. You
can also run it on demand from the Actions tab (workflow_dispatch). No manual
`wrangler deploy` is needed in normal operation.

It depends on just two **GitHub Actions secrets** (repo Settings → Secrets and
variables → Actions):

| Secret | What it is |
| --- | --- |
| `CLOUDFLARE_API_TOKEN` | Cloudflare API token with **Workers Scripts: Edit** + **R2 Storage: Read** permissions. A *different* credential from the S3-style `R2_ACCESS_KEY`/`R2_SECRET_KEY` the .NET app uses — those cannot deploy Workers. |
| `CLOUDFLARE_ACCOUNT_ID` | Your Cloudflare account ID (dash → right sidebar). |

```bash
gh secret set CLOUDFLARE_API_TOKEN  --body '<token>' --repo Hamza-Labs-Core/Daleel
gh secret set CLOUDFLARE_ACCOUNT_ID --body '<id>'    --repo Hamza-Labs-Core/Daleel
```

The Worker's `AUTH_TOKEN` is set to the **same value as `CLOUDFLARE_API_TOKEN`**
— there is no separate log-viewer secret. To read logs, present that token as
the bearer / Basic-auth password (see [Auth](#auth) and the examples above).
Auth stays on by design: this Worker exposes production logs over a public
`*.workers.dev` URL, so it must never be open to the world. If you truly want it
unauthenticated, see the warning at the end of this section.

### Manual (first-time setup / break-glass)

```bash
cd workers/log-viewer
npm install                 # optional; wrangler can run via npx
npx wrangler login          # one-time, unless CLOUDFLARE_API_TOKEN is set
npx wrangler secret put AUTH_TOKEN   # paste the CLOUDFLARE_API_TOKEN value
npx wrangler deploy
```

### Running it unauthenticated (NOT recommended)

The Worker fails closed: if `AUTH_TOKEN` is unset it returns 500, never serving
logs openly. If you accept that **anyone on the internet who finds the URL can
read all production logs** and want it open anyway, change `authorize()` in
[`src/index.js`](src/index.js) to `return null` when `env.AUTH_TOKEN` is unset,
then deploy without setting the secret. Prefer instead to keep auth on and put a
Cloudflare Access policy in front of the Worker if you want SSO-gated browser
access.

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
