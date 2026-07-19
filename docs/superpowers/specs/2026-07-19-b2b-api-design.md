# B2B API: expose the entity index + store monitoring as a paid product

**Product.** Two things businesses pay for:
1. **Data API** — read the entity index (items with all sellers' offers, prices, availability,
   images), stores, brands. The dedup work is the moat: one item, all sellers under it.
2. **Monitoring-as-a-service** — "keep store X's whole inventory synced" (spec
   2026-07-19-store-inventory-monitor): a client subscribes a store; we sync it; they read fresh
   inventory or receive webhooks. This is the recurring-revenue anchor.

## Accounts

- New `ApiClient` entity: `Id`, `OrgName`, `ContactEmail`, `OwnerUserId` (FK to Identity — a normal
  login owns the org), `PlanId` (FK `SubscriptionPlan`), `Status` (pending → active → suspended),
  `CreatedAt`. A client can hold multiple keys and multiple store monitors.
- Signup: normal `/register`, then "Request API access" on `/settings` → creates a *pending*
  `ApiClient` → admin approves on `/admin/api` (the existing global gate
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
- **Monitored-store sync spend is attributed to the subscribing client**: the sync units run under
  the client's id and the existing `CostEstimator` meters LLM/fetch spend against it. Shopify
  stores ≈ pennies (pure JSON); HTML stores cost LLM only on changed pages — the content-hash
  design keeps recurring cost ∝ change rate, which is what makes a flat per-store fee viable.
- Enforcement: per-minute rate limit (`ratelimit.api_per_minute`, per key), monthly call quota by
  plan (`429` + `X-RateLimit-*` headers; `402` when a plan lapses). Soft-warn at 80% via email.

## Pricing (seeded plans — numbers are admin-editable on /admin/plans)

Extend `SubscriptionPlan` (it already has `MonthlyCredits`) with `ApiCallsPerMonth`,
`MonitoredStores`, `WebhooksEnabled`, `MonthlyPriceUsd`:

| Plan | API calls/mo | Monitored stores | Webhooks | Price |
|---|---|---|---|---|
| Trial (14d) | 1,000 | 1 (Shopify only) | — | $0 |
| Starter | 50,000 | 1 | — | $49/mo |
| Growth | 500,000 | 5 | ✓ | $199/mo |
| Enterprise | custom | custom | ✓ | custom |

- Per-store economics: a Shopify store syncs for cents/day; an HTML store's LLM extraction on
  changed pages is bounded by the hash-skip — target gross margin ≥80% at the Starter fee, and
  the per-client sync-spend column on `/admin/api` makes a loss-making monitor visible immediately.
- Overage: hard-stop at quota by default (predictable for the client); optional metered overage
  per 1k calls for Growth+. Billing is invoice-first (manual, B2B); Stripe integration is a later
  step and out of scope here.

## Admin (`/admin/api`)

- Clients table: org, owner, plan, status, keys (prefix + last-used), calls this period,
  **sync spend this period**, monitors. Actions: approve/suspend client, issue/revoke/rotate key,
  change plan, adjust a client's monitor list.
- Read-only usage drill-down reuses the existing analytics/event-store charts filtered by client.
- Global kill-switch stays `feature.api_access_enabled`.

## Order of work

1. Schema: `ApiClient`, `ApiKey`, `ApiUsage` + `SubscriptionPlan` columns + `ApiCallLog.ClientId`.
2. Key middleware (hash lookup, scopes, rate/quota enforcement, metering stamp) + `/api/v1/items`,
   `/items/{id}`, `/stores`, `/brands` read endpoints.
3. `/admin/api` (clients, keys, usage) + request-access flow on `/settings`.
4. Monitors API on top of the inventory-monitor feature (its spec is the dependency) +
   per-client sync-spend attribution.
5. Webhooks (delta push from `inventory.finalize`) with signed payloads (HMAC per client secret).
6. QA: two test clients on different plans — quota exhaustion (429), suspended client (403),
   key rotation grace, monitor subscribe → inventory delta via API + webhook.
