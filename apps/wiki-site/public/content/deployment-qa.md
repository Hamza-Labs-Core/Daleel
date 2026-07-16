# Deployment & QA workflow

> How Daleel ships: two VPS environments (QA + PROD), a GHCR image, and three GitHub
> Actions pipelines. Everything here is drawn from the actual workflows
> (`.github/workflows/deploy.yml`, `deploy-qa.yml`, `reset-postgres.yml`, `ci.yml`), the
> compose/config under `deploy/`, and `Program.cs` — not from intent. When the code and
> this doc disagree, **the code wins**; fix the doc.

---

## 1. Two environments, one image

Daleel runs the same container image (`ghcr.io/hamza-labs-core/daleel`) on two separate
VPS boxes. They differ only in which SSH host they target, which image tag they run, and a
handful of env values written into `/opt/daleel/.env`.

| | **PROD** | **QA** |
|---|---|---|
| Hostname | `daleel.hamzalabs.dev` | `qa-daleel.hamzalabs.dev` |
| SSH host secret | `DEPLOY_SSH_HOST` | `QA_SSH_HOST` |
| Image tag | `latest` | `qa-pr-<PR number>` |
| Trigger | push to `main` (or `workflow_dispatch`) | a PR carrying the `qa` label |
| Workflow | `deploy.yml` | `deploy-qa.yml` |
| GitHub Environment | `production` (approval-gated) | `qa` (unprotected by default) |
| `CADDY_DOMAIN` | `daleel.hamzalabs.dev` | `QA_CADDY_DOMAIN` secret ⇒ `qa-daleel.hamzalabs.dev` |
| Postgres event DB | `daleel_events` (prod volume) | `daleel_events` (QA volume, its own password) |
| R2 buckets | `daleel-{logs,images,specs,data}` | `daleel-qa-{logs,images,specs,data}` (own token) |
| `DALEEL_ENV` | `prod` | `qa` |
| `DIAGNOSTICS_ENABLED` | unset (off) | `true` (the `/diagnostics` testbench) |
| `ACTOR_STEPS_DEFAULT` | unset (off) | `true` (exercises the LLM-actor steps) |

Both boxes are provisioned identically and self-contained: each deploy bootstraps Docker,
`/opt/daleel`, a `daleel.service` systemd unit, UFW, log rotation, and a GHCR login. On a
fresh box the first run provisions everything; on an already-provisioned box every check is
a fast no-op.

> **The `SSH_USER` and `SSH_KEY` are shared** across both environments (`DEPLOY_SSH_USER`,
> `DEPLOY_SSH_KEY`). Only the *host* differs — QA points at `QA_SSH_HOST`. Likewise all the
> LLM/provider keys (`OPENROUTER_API_KEY`, `SERPAPI_KEY`, `CONTEXT_DEV_API_KEY`,
> `GOOGLE_PLACES_API_KEY`, `APIFY_TOKEN`) are shared secrets.

---

## 2. The deploy flow

```mermaid
flowchart TD
    subgraph dev["Developer"]
        PR["Open PR → main"]
        LBL["Add 'qa' label"]
        MERGE["Merge to main"]
    end

    subgraph ci["ci.yml (push + PR)"]
        CIB["build -warnaserror"]
        CIT["dotnet test"]
        CID["docker build+push :latest\n(push to main only)"]
        CIB --> CIT --> CID
    end

    subgraph qa["deploy-qa.yml ('qa' label)"]
        QB["build -warnaserror + test"]
        QD["docker build+push :qa-pr-N"]
        QENV["render /opt/daleel/.env\n(QA secrets, qa buckets, DALEEL_ENV=qa)"]
        QSSH["scp config + deploy.sh\n→ QA_SSH_HOST"]
        QCOM["comment QA URL on PR"]
        QB --> QD --> QENV --> QSSH --> QCOM
    end

    subgraph prod["deploy.yml (push main / dispatch)"]
        PB["build-test (Ryuk-disabled, 20m cap)"]
        PDOCK["docker build+push :latest"]
        PGATE["production environment\n(manual approval)"]
        PENV["render /opt/daleel/.env\n(prod secrets, DALEEL_IMAGE=:latest)"]
        PSSH["scp config + deploy.sh\n→ DEPLOY_SSH_HOST\n(pull→restart→health-check→rollback)"]
        PB --> PDOCK --> PGATE --> PENV --> PSSH
    end

    PR --> ci
    PR --> LBL --> qa
    qa -.verify on qa-daleel.hamzalabs.dev.-> MERGE
    MERGE --> prod
    prod --> LIVE["daleel.hamzalabs.dev"]
    qa --> QLIVE["qa-daleel.hamzalabs.dev"]
```

---

## 3. CI (`ci.yml`)

Runs on every push to `main` and every PR against it. Three chained jobs:

1. **build** — `dotnet build Daleel.sln -c Release --no-restore -warnaserror`.
2. **test** — `dotnet test Daleel.sln -c Release`.
3. **docker** — builds and pushes `:latest` + `:sha-…`, **only** on push to `main`
   (`if: github.event_name == 'push' && github.ref == 'refs/heads/main'`), never on a PR.

The image is built **config-free**: the Dockerfile only runs `dotnet publish`. No API keys
or provider credentials are passed as build args — every secret is injected at *runtime* via
`/opt/daleel/.env`, which the deploy workflow writes on the VPS. If a build-time secret ever
becomes necessary, use BuildKit `--mount=type=secret`, never `build-args` (which bake into an
image layer).

### The `-warnaserror` + MudBlazor gate

> **CI builds `-c Release -warnaserror` with the MudBlazor analyzer promoted to errors
> (MUD0002).** Local Debug builds miss these — a `.razor` change can pass locally and then
> **fail the deploy build**. Always run `dotnet build Daleel.sln -c Release -warnaserror`
> before pushing Razor changes. (Common trap: `MudChipSet` has no `Mandatory` property.)

This same gate is the first job of both `deploy.yml` and `deploy-qa.yml`, so a build
warning blocks the deploy, not just CI.

---

## 4. Production deploy (`deploy.yml`)

Triggers on **push to `main`** (auto-deploy) or manual `workflow_dispatch` (with an optional
`image_tag`, default `latest`).

```
concurrency:
  group: deploy-production
  cancel-in-progress: true
```

A newer push cancels an older in-flight deploy so a stalled run can never jam the queue — a
Testcontainers/Ryuk hang once left a run *In Progress* for 6.5h, blocking every later deploy.
The latest commit is the one we want live anyway.

Jobs:

1. **build-test** — restore, `build -warnaserror`, then `dotnet test` with
   `TESTCONTAINERS_RYUK_DISABLED=true` and `--blame-hang-timeout 5m --blame-hang-dump-type
   none`. Hard `timeout-minutes: 20`. (The `Web.Tests` suite spins up Postgres via
   Testcontainers; its Ryuk reaper can stall on the CI Docker socket and hang silently — the
   Ryuk-disable + blame-hang flags are the documented mitigations.)
2. **docker** — Buildx, GHCR login, `metadata-action` (`latest` + `sha`), build & push with
   GHA layer cache.
3. **deploy** — gated by the **`production` GitHub Environment** (configure required reviewers
   under Settings → Environments → production). Steps:
   - **Validate required secrets** — fails fast, *before any box mutation*, if any of
     `OPENROUTER_API_KEY`, `SERPAPI_KEY`, `CONTEXT_DEV_API_KEY`, `GOOGLE_PLACES_API_KEY`,
     `APIFY_TOKEN`, `DEEPL_API_KEY`, `POSTGRES_PASSWORD`, `DEPLOY_SSH_{HOST,USER,KEY}` is
     unset/empty/`CHANGE_ME`. Secrets are mapped to env vars (not interpolated into the
     script body) so multi-line values (the SSH key!) and shell metacharacters are safe.
     Warns (not fails) if R2 creds are set without an images public host.
   - **Bootstrap VPS** (idempotent) — installs Docker Engine + Compose, creates
     `/opt/daleel`, installs the `daleel.service` systemd unit (`docker compose up -d
     --wait`, survives reboots), opens UFW for 22/80/443 (+443/udp for HTTP/3), configures
     Docker log rotation + logrotate, `docker login ghcr.io`. Replaces the old manual
     `setup.sh` entirely.
   - **Resolve Cloudflare endpoints** — derives the worker fleet URLs + poll-queue id from
     the Cloudflare API by subdomain/name, so no per-URL secrets are needed. A CF API
     incident must never abort the app deploy — it warns and leaves the endpoints empty
     (the app then runs the inline pipeline).
   - **Write `/opt/daleel/.env`** from current GitHub secrets (`umask 077`, quoted heredoc so
     values land verbatim, installed `0600`).
   - **Stage + install config** — `scp` `Caddyfile` / `docker-compose.yml` / `deploy.sh` to
     the box, install them, run `./deploy.sh "$DALEEL_TAG"` (pull → restart → health-check →
     rollback). The normal path **never** touches `postgres_data`.

### `DALEEL_IMAGE` is pinned on every deploy

`.env` rewrites `DALEEL_IMAGE=ghcr.io/hamza-labs-core/daleel:latest` on **every** prod
deploy. This matters: `deploy.sh` only `export`s the tag for its own run, but the **persisted
`.env`** is what `docker compose up` and the systemd unit read on every restart/reboot.
Production once got stuck on `qa-pr-8` because a stale `DALEEL_IMAGE` line survived in the
file — build/push went green while compose kept resolving the stale tag. Rewriting it every
deploy evicts any leftover `qa-pr-N` and keeps prod on `latest`.

---

## 5. QA deploy (`deploy-qa.yml`)

Triggers on `pull_request` events `[labeled, synchronize, reopened]` — i.e. **when a PR
carries the `qa` label**, and re-deploys on each new commit while the label stays on.

```
env:
  QA_TAG: qa-pr-${{ github.event.pull_request.number }}
concurrency:
  group: deploy-qa-${{ github.event.pull_request.number }}
  cancel-in-progress: true
```

It mirrors `deploy.yml` but:

- Guards `if: contains(labels, 'qa') && head.repo == this repo` — a fork PR (no secrets) can
  never run a secret-bearing deploy.
- Publishes a **PR-scoped tag** (`qa-pr-N` + `sha`), deliberately **never `latest`** — that
  tag is production's.
- Targets `QA_SSH_HOST`, deploys to the `qa` GitHub Environment, and comments the QA URL back
  on the PR when it succeeds.
- Renders a QA-flavoured `.env`: its **own** R2 token (`R2_QA_ACCESS_KEY`/`SECRET_KEY`) scoped
  to the `daleel-qa-*` buckets, its own `QA_POSTGRES_PASSWORD` (default `daleel-qa-events`),
  `DALEEL_ENV=qa`, `DIAGNOSTICS_ENABLED=true`, `ACTOR_STEPS_DEFAULT=true`, and the QA admin
  allowlist (`DALEEL_ADMIN_EMAILS`).

> The QA R2 isolation is not cosmetic: sharing one token + one set of buckets is what once let
> a QA-intended R2 purge wipe **prod's** objects. The `R2_QA_*` token is scoped to the
> `daleel-qa-*` buckets only.

### The team model: QA tracks main

The current `deploy-qa.yml` in this tree is **label-driven** — a `qa`-labelled PR builds
`qa-pr-N` and ships it to the single QA box. The team convention layered on top of that
(see the repo `CLAUDE.md`) is:

- **QA should always reflect `main`** (latest fixes), never a stale isolated PR branch. In
  practice that means keeping the labelled branch merged up with `main`.
- The **`qa` label is an opt-in preview** — a way to put *one* branch on the QA box.

### Integration-branch workflow (and the leapfrog hazard)

There is **one QA server**. Two independently `qa`-labelled PRs deploy to the *same* box, so
each push **leapfrog-reverts** the other's features — whoever pushed last is what's live.

The rule for several feature PRs in flight:

1. Cut **one integration branch** off `main`.
2. PR every feature **into** the integration branch.
3. Put the `qa` label **only** on the integration branch.
4. Merge the integration branch to `main` **last**.

> **Merging a base branch auto-closes the PRs that target it.** So the integration branch
> merges to `main` last, and stacked branches are updated by **merging `main` in — never
> rebasing** (rebasing a shared stacked branch rewrites history the dependents are built on).

### "Done" means verified on QA

For a `qa`-labelled branch, *done* is: push → auto-deploy → **verify the feature live on
`qa-daleel.hamzalabs.dev`** (run a real search; a screenshot is the evidence). "Local
`dotnet test` green" is **not** done — the deploy-only breakages in §7 hide precisely where
local tests can't see them.

---

## 6. Postgres (the `daleel` DB) & the reset workflow

Daleel is **PostgreSQL-only** — there is no SQLite or in-memory fallback. The migration off
SQLite is complete; a prod symptom of that migration is worth remembering: "login failing"
on prod was **lost accounts** (the EF migration copied schema, not rows), not an auth bug.

Two logical databases live on the same Postgres server:

- **`daleel`** — the app DB (users, search jobs, brands/stores/models, scraped prices,
  `ApiCallLog`, moderation logs…). `DaleelDbContext.Database.Migrate()` at startup **creates
  it if absent** and applies pending migrations. Name overridable via `POSTGRES_APP_DATABASE`.
- **`daleel_events`** — the append-only event store (`PipelineEvent` + `SystemEvent`). The
  bundled compose `postgres` service seeds only `daleel_events`; the first app boot provisions
  `daleel`. Its migration is best-effort (a transient failure degrades to dropping events,
  never stops the app).

`POSTGRES_PASSWORD` is the **single source of truth**: the connection string is *derived* from
it at `.env` render time (`Host=postgres;…;Username=daleel;Password=<pw>`). A hand-maintained
`POSTGRES_CONNECTION_STRING` once drifted to `Username=postgres` — a user the container never
creates — and broke auth with `28P01`. Synthesizing the string guarantees user/host/db/pw
always match the actual container.

### Rotating the password needs the volume wiped

Postgres bakes the password into `daleel_postgres_data` on **first init only**. After you
rotate `POSTGRES_PASSWORD`, the volume still holds the *old* password and the app can no
longer authenticate. Fixing it is a deliberate, manual, **destructive** action:

- Run the standalone **`reset-postgres.yml`** (`workflow_dispatch`). It shares the
  `deploy-production` concurrency group (`cancel-in-progress: false`) so it queues rather than
  racing a deploy, runs in the approval-gated `production` environment, then:
  `docker compose down` → `docker volume rm daleel_postgres_data` → `docker compose up -d`.
- **This erases the event store.** The normal deploy path *never* touches the volume — an
  earlier marker-file-guarded `docker volume rm` in `deploy.sh` was a data-loss landmine (a
  lost marker would let an ordinary push destroy the store) and was removed in favour of this
  isolated workflow.

---

## 7. R2 object storage: shared connection, isolated buckets

R2 is optional — unset creds fall back to file logging at `/app/data/logs/`. When configured,
one bucket per concern:

| Bucket | Purpose | Public host needed? |
|---|---|---|
| `daleel-logs` | Warning+ JSON-Lines error logs | never public |
| `daleel-images` | Product/brand images | **yes** — `R2_PUBLIC_URL_IMAGES`; blank ⇒ images hot-linked from source (the S3 endpoint 403s a plain `<img>` GET) |
| `daleel-specs` | Spec sheets | optional (DB copy stays canonical) |
| `daleel-data` | Self-contained entity JSON docs (source of truth for rich entities) | optional |

> **PROD and QA share the R2 *connection* (same Cloudflare account/endpoint) but have separate
> buckets, hosts and databases.** PROD writes `daleel-*`; QA writes `daleel-qa-*` under a
> bucket-scoped `R2_QA_*` token. This keeps QA reads/writes from ever touching prod objects.

`R2_ENDPOINT` is normally unnecessary — the app derives it from `CLOUDFLARE_ACCOUNT_ID`. The
legacy single-bucket `R2_BUCKET_NAME` / `R2_PUBLIC_URL` still seed the logs bucket / images
host as fallbacks.

---

## 8. Deploy-only build invariants

These break the deploy *after* a merge while local Debug builds stay green — guard them:

- **Dockerfile csproj-COPY list must match `Daleel.sln`.** The Dockerfile copies each
  project's `.csproj` before `dotnet restore`; add a new project to the solution and you must
  add its `COPY` line, or the Docker build fails on a missing csproj even though local restore
  worked.
- **New compose services need env wired into `deploy.yml`'s `.env` render.** A service added
  to `deploy/docker-compose.yml` that reads an env var gets an *empty* value in prod unless
  that var is added to the `Write /opt/daleel/.env` heredoc in **both** `deploy.yml` and
  `deploy-qa.yml`. The config-free-image rule means runtime env is the *only* injection point.
- **`-warnaserror` + MUD0002** (see §3) — a Razor warning is a deploy failure.

---

## 9. Debugging a QA run

- **`/admin/workflows`** → click a run for its step timeline; the event-name histogram reveals
  which stage produced or lost data. A failed run surfaces its real exception on the job row.
- **`/admin/queues`** → the dead-unit ledger with reason strings — grep the reason into the
  handler.
- **`/admin/timeline`** → the unified system-event feed.
- **"No results just yet" can be a FAULTED run**, not an empty one — check the run status
  server-side, not just the UI.
- **The log-viewer Worker reads PROD `daleel-logs` only.** For QA, use SSH `docker logs`
  (1Password-gated key) or the admin UI. `/logs/search` gotchas: `since=` is file-level,
  the 1000-line cap is oldest-first (use rare terms), and the query is a substring match.
- UI automation: the **Playwright MCP** admin profile drives MudBlazor; CDP-synthetic typing
  does not bind Blazor server state (clicks do). `/?Q=<query>` pre-fills the search box only
  on a **hard** page load.

See [Observability & admin](/observability-admin) for the admin pages in detail.

---

## Key files

| Path | What it is |
|------|------------|
| `.github/workflows/ci.yml` | Build (`-warnaserror`) → test → docker (main only). The `-warnaserror`/MUD0002 gate. |
| `.github/workflows/deploy.yml` | Production pipeline: push-to-main/dispatch → build-test → docker `:latest` → approval-gated SSH deploy to `DEPLOY_SSH_HOST`. |
| `.github/workflows/deploy-qa.yml` | QA pipeline: `qa`-labelled PR → `qa-pr-N` image → SSH deploy to `QA_SSH_HOST`, QA buckets/DB/token, PR-comment URL. |
| `.github/workflows/reset-postgres.yml` | Manual, destructive, approval-gated wipe of `daleel_postgres_data` (after a password rotation). |
| `deploy/docker-compose.yml` | The prod/QA compose stack (app + `postgres` + Caddy). Bind-mounted, `scp`-synced each deploy. |
| `deploy/Caddyfile` | Reverse proxy + automatic TLS; `CADDY_DOMAIN` per environment. |
| `deploy/deploy.sh` | On-box: pull → restart → health-check → rollback. |
| `deploy/.env.example` | Canonical list of every runtime env var. |
| `src/Daleel.Web/Program.cs` | `EnsureDatabase` (creates `daleel`, migrates event store), DI wiring, `appConnection` resolution. |
| `src/Daleel.Web/Events/PostgresConnection.cs` | Resolves the app DB (`daleel`, `POSTGRES_APP_DATABASE`) vs event DB (`daleel_events`) from `POSTGRES_CONNECTION_STRING` / `DATABASE_URL`. |
| `src/Daleel.Web/Data/QuotaService.cs` | Pre-search credit gate (post-hoc charge; never kills an in-flight job). |
| `CLAUDE.md` | The branching/QA conventions (integration branch, leapfrog rule, "done = verified on QA"). |
