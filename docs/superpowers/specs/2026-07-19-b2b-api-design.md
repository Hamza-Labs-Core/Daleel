# B2B API: expose the entity index + store monitoring as a paid product

**The split is CONSUMER vs APPLICATIONS.** The consumer product serves people in the UI. The
data API serves registered **Applications** — there are no "users" on the API side: an
Application is the account, the key holder, the credit holder, the monitor subscriber. A person
(the developer) only appears as the Application's owner login for the portal.

**Product.** Two things applications pay for:
1. **Data API** — read the entity index (items with all sellers' offers, prices, availability,
   images), stores, brands. The dedup work is the moat: one item, all sellers under it.
2. **Monitoring-as-a-service** — "keep store X's whole inventory synced" (spec
   2026-07-19-store-inventory-monitor): a client subscribes a store; we sync it; they read fresh
   inventory or receive webhooks. This is the recurring-revenue anchor.

## Accounts

- New `Application` entity: `Id`, `Name`, `ContactEmail`, `OwnerUserId` (FK to Identity — the
  developer's login, portal-only), `ApiPlanId`, `Status` (pending → active → suspended),
  `CreatedAt`. An Application holds keys, a credit ledger, monitors, webhooks. One owner login
  can own several Applications (per product/environment: `acme-prod`, `acme-staging`).
- Signup: normal `/register`, then "Request API access" on `/settings` → creates a *pending*
  `Application` → admin approves on `/admin/api` (the existing global gate
  `feature.api_access_enabled` stays the kill-switch). No self-serve activation at first —
  B2B deals in MENA start with a conversation anyway.

## Auth

- **API keys, ours** (the "NO user API keys" invariant is about upstream provider keys — issuing
  our own keys to customers is the product). Format `dlk_live_<32 bytes url-safe>`; only a SHA-256
  hash is stored (`ApiKey`: `ClientId`, `Hash`, `Prefix` (first 8 chars, for display), `Scopes`,
  `CreatedAt`, `RevokedAt?`, `LastUsedAt`).
- Sent as `Authorization: Bearer dlk_…`. Middleware resolves hash → client → plan/quota, stamps
  `ClientId` into `HttpContext` for metering. Rotation mirrors the VPS token-authority pattern:
  issue new, old key keeps working for a grace window, then revoke.
- **Scopes**: `items:read`, `stores:read`, `brands:read`, `inventory:read`, `monitors:manage`,
  `webhooks:manage`. Keys default to read-only.

## Endpoints (v1, read-mostly, minimal-API under `/api/v1`)

| Route | What |
|---|---|
| `GET /items?q=&geo=&category=&brand=&store=&page=` | entity index (live rows only — aliases excluded) |
| `GET /items/{id}` | full R2 document: specs, all offers (seller, price, availability, offer images) |
| `GET /stores` / `GET /stores/{id}` | store profiles |
| `GET /stores/{id}/inventory?since=` | a monitored store's catalogue with prices/availability; `since` returns the delta |
| `GET /brands` / `GET /brands/{id}` | brand profiles + catalogue models |
| `POST /monitors` `{storeUrlOrId, cadenceHours}` | subscribe a store (scope `monitors:manage`; quota-gated by plan) |
| `GET /monitors` / `DELETE /monitors/{id}` | manage own monitors |
| `POST /webhooks` `{url, events:[inventory.delta, price.change]}` | delta push — fed by the sync's `inventory.finalize` SystemEvent |

Versioned path (`/api/v1/`), JSON only, cursor pagination, `ETag`/`If-None-Match` on documents
(they're content-hashed R2 objects — free 304s).

## Cost model & metering

- Every call is metered through the existing event-store path (`ApiCallLog` gains `ClientId`),
  rolled into a per-period `ApiUsage` row (client, period, calls, sync-spend). The admin analytics
  pages already read this store.
- **Sync spend is metered per STORE, not per application** (a store synced once may serve N
  subscribers); each subscriber is charged the credit PRICE of the monitor, and `/admin/api`
  shows store sync-cost vs subscriber credit revenue side by side. Shopify
  stores ≈ pennies (pure JSON); HTML stores cost LLM only on changed pages — the content-hash
  design keeps recurring cost ∝ change rate, which is what makes a flat per-store fee viable.
- Enforcement: per-minute rate limit (`ratelimit.api_per_minute`, per key), monthly call quota by
  plan (`429` + `X-RateLimit-*` headers; `402` when a plan lapses). Soft-warn at 80% via email.

## Pricing — credit-based, with a SEPARATE B2B credit ledger

**B2B credits are a different currency from consumer search credits.** The consumer pool
(`SubscriptionPlan.MonthlyCredits` → `UserSubscription`/`UserQuota`, charged per search) stays
untouched — different product, different price points, user-level. B2B credits are ORG-level:

- New `ApiPlan` (not `SubscriptionPlan`): `Name`, `MonthlyApiCredits`, `MaxMonitoredStores`,
  `WebhooksEnabled`, `MonthlyPriceUsd`. Seeded separately; edited on `/admin/api`, not
  `/admin/plans` (that page stays consumer-only).
- New `ApiCreditLedger` per `Application`: period grants, per-action debits, top-up packs — the same
  *mechanism shape* as the consumer quota code (reuse the patterns, share no rows or balances).
  A user who is both a consumer and an org owner has two unrelated balances.

Everything B2B draws from that one org pool, not flat per-resource fees. Why credits:

- **A store can be monitored by N clients but is synced ONCE.** The sync is shared infrastructure;
  each subscriber pays the ACCESS price in credits while our cost stays single-sync — the second
  subscriber to a store is nearly pure margin, and no client ever pays "per our cost", so
  attribution stays simple and un-gameable.
- **Stores cost wildly different amounts** (Shopify JSON ≈ free; HTML+LLM heavier). Credits price
  that honestly per store instead of one flat fee subsidizing the expensive ones.

Charge sheet (admin-editable `pricing.api.*` SystemConfig rows, like the existing `pricing.*`):

| Action | Credits |
|---|---|
| `GET /items` (list page) | 1 |
| `GET /items/{id}` (full doc) | 2 |
| stores/brands reads | 1 |
| `GET /stores/{id}/inventory` | 5 |
| Monitor subscription, per store per month | by sync class: `shopify` 500 · `html` 2,000 |
| Webhook delivery | 1 |

Monitor charges post monthly per subscription (not per sync — cadence is ours to optimize).
The sync's ACTUAL spend is still metered per store on `/admin/api` (CostEstimator), so a
loss-making sync class is visible and its credit price adjustable without a deploy.

API plans grant B2B credits (seeded, admin-editable on /admin/api):

| ApiPlan | B2B credits/mo | Max monitored stores | Webhooks | Price |
|---|---|---|---|---|
| Trial (14d) | 2,000 | 1 (shopify class) | — | $0 |
| Starter | 60,000 | 2 | — | $49/mo |
| Growth | 600,000 | 10 | ✓ | $199/mo |
| Enterprise | custom | custom | ✓ | custom |

Hard-stop at zero credits (`402`, monitors pause at period end, never mid-cycle); top-up credit
packs for Growth+; invoice-first billing, Stripe later. Max-stores stays a plan cap (an abuse
bound), but the SPEND is all credits.

## Two sites: daleel vs api.daleel

Like claude.ai vs console.anthropic.com, the split is physical — separate hosts:

- **`daleel.hamzalabs.dev`** — the consumer product only. No portal, no `/api/v1`.
- **`api.daleel.hamzalabs.dev`** — the Applications product: `/api/v1/*` (key-auth), the
  developer portal (`/` = console: applications, keys, credits, monitors, webhooks), and the API
  docs/reference. QA mirror: `api.qa-daleel.hamzalabs.dev`.

Implementation: ONE binary, host-gated route groups (consumer Blazor pages bound to the main
host; API endpoints + portal pages bound to the api host — a wrong-host request 404s). Caddy adds
the second vhost to the same container (one cert each); deploy.yml renders both domains. Shared
DB/DI stays one deployment — the separation is product/host-level, not infrastructure-level,
until scale says otherwise.

## API level vs UI level — the Anthropic model

Shape it exactly like Anthropic's split: **claude.ai** (consumer app, its own subscription) vs
**console.anthropic.com** (org console: prepaid API credits, keys, workspaces, usage, tier
limits) — same vendor, two products, two currencies, one login. For us: the consumer app stays
as-is; the **portal is our Console** — org-scoped, prepaid B2B credits, keys with scopes,
usage burn-down, rate-limit tier visible. Future (not v1): workspaces under an org for per-team
keys/attribution, like Console workspaces.

Two surfaces per application, same org account, strictly separated:

- **API level** (`/api/v1/*`, key-authenticated, credit-metered): the machine surface — data
  reads, inventory, monitors, webhooks. Everything here debits the org's B2B credit ledger and is
  scope-gated per key. No HTML, no cookies, no Blazor circuit.
- **UI level — the client portal** (`/portal`, cookie-authenticated as the org OWNER user,
  `[Authorize]`, FREE — portal actions never burn credits): self-serve org dashboard:
  - credit balance + burn-down chart, calls this period, per-endpoint usage;
  - keys: create/revoke/rotate (secret shown once), scopes, last-used;
  - monitors: subscribe/cancel stores, sync status, last delta;
  - webhook endpoints + delivery log; plan + invoices.
  Managing a monitor in the portal and via `POST /monitors` are the SAME operations on the same
  rows — the portal is a UI over the application's own API-level objects, never a second code path.
  (Monitor SUBSCRIPTION credits are charged identically whichever surface created them; only
  reads/browsing in the portal are free.)
- **Consumer UI** (existing app) stays credit-separate entirely: a shopper browsing /items pays
  nothing and never touches B2B metering; the B2B API never serves the consumer UI.

## Admin (`/admin/api`)

- Applications table: org, owner, plan, status, keys (prefix + last-used), calls this period,
  **sync spend this period**, monitors. Actions: approve/suspend application, issue/revoke/rotate key,
  change plan, adjust a client's monitor list.
- Read-only usage drill-down reuses the existing analytics/event-store charts filtered by client.
- Global kill-switch stays `feature.api_access_enabled`.

## Order of work

1. Schema: `Application`, `ApiKey`, `ApiPlan`, `ApiCreditLedger`, `ApiUsage` + `ApiCallLog.ClientId` (consumer `SubscriptionPlan` untouched).
2. Key middleware (hash lookup, scopes, rate/quota enforcement, metering stamp) + `/api/v1/items`,
   `/items/{id}`, `/stores`, `/brands` read endpoints.
3. `/admin/api` (clients, keys, usage) + request-access flow on `/settings`.
3b. Client portal `/portal` (balance, keys, monitors, webhooks — free UI over the application's own objects).
4. Monitors API on top of the inventory-monitor feature (its spec is the dependency) +
   per-client sync-spend attribution.
5. Webhooks (delta push from `inventory.finalize`) with signed payloads (HMAC per application secret).
6. QA: two test applications on different plans — quota exhaustion (429), suspended application (403),
   key rotation grace, monitor subscribe → inventory delta via API + webhook.
