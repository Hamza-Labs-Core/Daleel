# Daleel Wiki

Developer documentation for **Daleel** — a product / price-comparison search app built on
Blazor Server (MudBlazor), EF Core, SignalR, and an Elsa-based search pipeline backed by
Context.dev.

## Wiki pages

| # | Page | Status | Contents |
|---|------|--------|----------|
| 01 | Overview & architecture | _planned_ | High-level system map, projects, hosting. See [architecture.md](../architecture.md) for now. |
| 02 | Search pipeline | _planned_ | Elsa workflow, jobs, providers, enrichment. See [SEARCH_WORKFLOW.md](../SEARCH_WORKFLOW.md) for now. |
| 03 | Data model & persistence | _planned_ | EF entities (Brand, Store, BrandModel, ScrapedPrice, SearchJob), repositories, event store. |
| **04** | **[UI components & UX flow](04-ui-components.md)** | **✅ written** | Pages, search/progress UX, product cards, detail pages, comparison, admin, settings, nav, localization, SignalR, theme. |

> Only **04 — UI components & UX flow** exists today; the other rows are placeholders for
> the planned series and point at the existing top-level docs in the meantime.

## What's on page 04

[04 — UI components & UX flow](04-ui-components.md) covers:

1. **Page structure** — every `.razor` page, route, and guard.
2. **Home / Search** — search bar, three-stage market detection (query → geolocation → ask),
   the 8-stage progress stepper, adaptive results.
3. **Product cards** — `SafeImage` (fail-open + opt-in blur), `ModelCard`, `ShoppingCard`,
   `ProductListings`.
4. **Detail pages** — `ProductDetail` / `BrandDetail` / `StoreDetail`, all DB-backed via
   repositories, stable-ID routing.
5. **Comparison** — schema-driven `ComparisonTable`, best-value highlighting, spec
   sanitization.
6. **Admin** — `AdminUsage`, `AdminWorkflows`, `AdminData` (R2 browser), and the rest.
7. **Settings** — search-history management + account deletion only.
8. **Navigation** — responsive `MudDrawer`, app bar, `NavMenu`, `UserMenu`.
9. **Localization** — EN/AR, the `/set-language` cookie flow, three-level RTL.
10. **SignalR** — `SignalRConversationBroadcaster`'s two-tier fan-out (hub + in-process
    notifier).
11. **Theme** — `DaleelTheme`, OS-preference detection, the manual-toggle nuance.

## Related top-level docs

- [architecture.md](../architecture.md) — system architecture.
- [SEARCH_WORKFLOW.md](../SEARCH_WORKFLOW.md) — Elsa search pipeline & persistent profiles.
- [UI_TEST_CASES.md](../UI_TEST_CASES.md) — UI test scenarios.

## Conventions

- Pages → `src/Daleel.Web/Components/Pages/`; shared components →
  `src/Daleel.Web/Components/Shared/`; layout → `src/Daleel.Web/Components/Layout/`.
- The app is **Blazor Server, interactive-only** (no WASM/Auto).
- Keep these pages in sync with source when routes, the SignalR contract, or the progress
  stages change.
