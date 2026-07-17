# UI Components & UX Flow

> **Scope:** This page documents the Blazor front-end of `Daleel.Web` — every page, the
> key shared components, the search/progress UX, real-time updates over SignalR, theming,
> and localization. It is written against the actual source under
> `src/Daleel.Web/Components/` and `src/Daleel.Web/Services/`. File references are clickable
> and include line numbers where they help.

**Stack at a glance**

- **Blazor Server, interactive-only.** The app render mode is `InteractiveServer`, set
  globally in `App.razor`. There is no WebAssembly / Auto mode — pages inject server-only
  services, so they only render under the Server renderer.
- **MudBlazor** for the component library (app bar, drawer, tables, dialogs, theme).
- **SignalR** for real-time search progress (a dedicated hub plus the in-process Blazor
  circuit).
- **EF Core** (`DaleelDbContext`) for the catalogue/detail data, plus an in-memory
  `AgentAnswer` JSON payload streamed from the search worker for fresh results.

---

## 1. Page structure

All pages live under [`Components/Pages/`](../../src/Daleel.Web/Components/Pages). Routes
are declared with `@page`; authorization with `@attribute [Authorize]`. Auth pages use
`[StaticSsrPage]` so antiforgery-token POST forms and the auth-cookie handshake work
without a Blazor circuit intercepting the submit.

### Public & user pages

| Page | Route | Guard / notes | What it does |
|------|-------|---------------|--------------|
| [Home.razor](../../src/Daleel.Web/Components/Pages/Home.razor) | `/` | `@implements IDisposable` | Main search/chat surface. Landing page when signed out, search workspace when authenticated. |
| [Brand.razor](../../src/Daleel.Web/Components/Pages/Brand.razor) | `/brand` | `AgentPageBase` | Brand analysis search — market presence & reputation. |
| [Compare.razor](../../src/Daleel.Web/Components/Pages/Compare.razor) | `/compare` | `AgentPageBase` | Side-by-side product comparison. |
| [Deals.razor](../../src/Daleel.Web/Components/Pages/Deals.razor) | `/deals` | `AgentPageBase` | Deal finder for a product. |
| [Stores.razor](../../src/Daleel.Web/Components/Pages/Stores.razor) | `/stores` | `AgentPageBase` | Store finder, location-aware. |
| [Monitor.razor](../../src/Daleel.Web/Components/Pages/Monitor.razor) | `/monitor` | `[Authorize]`, `AgentPageBase` | Create price monitors for keywords. |
| [ProductDetail.razor](../../src/Daleel.Web/Components/Pages/ProductDetail.razor) | `/product/{Id}` | — | Product detail: specs, images, brand reputation, where-to-buy, price history. |
| [BrandDetail.razor](../../src/Daleel.Web/Components/Pages/BrandDetail.razor) | `/brand/{Id}` | — | Brand profile: logo, reputation, pros/cons, catalogue of models. |
| [StoreDetail.razor](../../src/Daleel.Web/Components/Pages/StoreDetail.razor) | `/store/{Id}` | — | Store profile: rating, contact, hours, brands carried, map. |
| [History.razor](../../src/Daleel.Web/Components/Pages/History.razor) | `/history` | `[Authorize]` | Full search history with open / rerun / delete. |
| [Saved.razor](../../src/Daleel.Web/Components/Pages/Saved.razor) | `/saved` | `[Authorize]` | Bookmarked results; export to JSON. |
| [Settings.razor](../../src/Daleel.Web/Components/Pages/Settings.razor) | `/settings` | `[Authorize]` | Search-history management + account deletion (see §7). |
| [Account.razor](../../src/Daleel.Web/Components/Pages/Account.razor) | `/account` | `[Authorize]` | Quota/billing dashboard; danger-zone delete. |
| [Pricing.razor](../../src/Daleel.Web/Components/Pages/Pricing.razor) | `/pricing` | — | Subscription tiers (shares `PricingTiers`). |
| [Faq.razor](../../src/Daleel.Web/Components/Pages/Faq.razor) | `/faq` | — | Localized FAQ, including the halal content filter. |
| [Status.razor](../../src/Daleel.Web/Components/Pages/Status.razor) | `/status` | — | Public provider/service health. |
| [Login.razor](../../src/Daleel.Web/Components/Pages/Login.razor) | `/login` | `[StaticSsrPage]`, `AuthLayout` | Sign-in form. |
| [Register.razor](../../src/Daleel.Web/Components/Pages/Register.razor) | `/register` | `[StaticSsrPage]`, `AuthLayout` | Account creation. |
| [Logout.razor](../../src/Daleel.Web/Components/Pages/Logout.razor) | `/logout` | `[StaticSsrPage]`, `AuthLayout` | Sign-out confirmation. |
| [ForgotPassword.razor](../../src/Daleel.Web/Components/Pages/ForgotPassword.razor) | `/forgot-password` | — | Placeholder — directs to support until email sender is wired. |
| [Diagnostics.razor](../../src/Daleel.Web/Components/Pages/Diagnostics.razor) | `/diagnostics` | `[Authorize(Roles="Admin")]` | QA testbench for Context.dev endpoints; gated by `DIAGNOSTICS_ENABLED`. |
| [Error.razor](../../src/Daleel.Web/Components/Pages/Error.razor) | `/Error` | — | Standard ASP.NET Core error page. |

> **`AgentPageBase`** is the shared base for the secondary search pages (Brand, Compare,
> Deals, Stores, Monitor). They share a pattern: a query input, a [`ProgressLog`](../../src/Daleel.Web/Components/Shared/ProgressLog.razor)
> feed (simpler than the Home stepper), a `Run` method that calls
> `IConversationService.SubmitAsync(...)`, and a feature-specific results view
> (`BrandReputationView`, `ComparisonTable`, `StoreCards`, …).

### Admin pages

All admin pages carry `@attribute [Authorize(Roles = "Admin")]`. They live under
[`Components/Pages/Admin/`](../../src/Daleel.Web/Components/Pages/Admin).

| Page | Route | What it shows |
|------|-------|---------------|
| [AdminDashboard.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminDashboard.razor) | `/admin` | KPIs: total users, new this week/month, searches today/week/month, active subscriptions by plan, top queries (30d). |
| [AdminUsage.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminUsage.razor) | `/admin/usage` | Provider API usage & cost (see §6). |
| [AdminWorkflows.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminWorkflows.razor) | `/admin/workflows` | Search-job execution log + failure drill-down (see §6). |
| [AdminData.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminData.razor) | `/admin/data` | Cloudflare R2 object-storage browser (see §6). |
| [AdminAnalytics.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminAnalytics.razor) | `/admin/analytics` | Search trends (14d), type breakdown, geo distribution, cost & provider health, LLM token usage, most expensive queries. |
| [AdminUsers.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminUsers.razor) | `/admin/users` | User table: plan, searches used, join/last-active, admin/disabled. Toggle role, enable/disable, change plan. |
| [AdminBrands.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminBrands.razor) | `/admin/brands` | Cached brand profiles; refresh one or all stale (30d). |
| [AdminStores.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminStores.razor) | `/admin/stores` | Cached store profiles; refresh one or all stale (30d). |
| [AdminPlans.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminPlans.razor) | `/admin/plans` | CRUD for subscription plans (credits, price, features). |
| [AdminSettings.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminSettings.razor) | `/admin/settings` | System config key/value editor (rate limits, flags, per-plan model defaults). |
| [AdminModeration.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminModeration.razor) | `/admin/moderation` | Content-filter stats (30d): items filtered, top categories. |
| [AdminFiltered.razor](../../src/Daleel.Web/Components/Pages/Admin/AdminFiltered.razor) | `/admin/filtered` | Halal-filter audit log: last 200 removals (rule, content, query). Anonymous by design. |

---

## 2. Home / Search page

[`Home.razor`](../../src/Daleel.Web/Components/Pages/Home.razor) is the heart of the app.
Signed-out users see the [`LandingPage`](../../src/Daleel.Web/Components/Shared/LandingPage.razor);
authenticated users get the search workspace.

### Search bar

The input is a plain `MudTextField` (not a specialized search box):

- Two-way bound to a `_input` field.
- Placeholder is localized: `@(L["Search.Placeholder"])`.
- **Enter** (without Shift) → `OnKey()` → `SubmitAsync()`. The send-icon button also calls
  `SubmitAsync()`.
- Disabled while a search is running (`_running`) or when the input is whitespace.

### Market / geo detection

Market detection runs in a strict priority order — there is **no silent fallback to a
default**; if it can't be inferred, the user is asked.

1. **Query-text detection.** `GeoProfiles.DetectInText(query)?.Key` parses geo references
   in the query (e.g. *"AC in Dubai"* → `AE`).
2. **Browser geolocation** (`DetectFromLocationAsync()`), only if text detection returns
   null. It calls `BrowserStore.DetectMarketFromLocationAsync()`, which invokes the
   `daleelGetLocation()` JS function (`wwwroot/daleel.js`) → `navigator.geolocation.getCurrentPosition()`
   with an ~8s timeout. Coordinates map to the nearest supported market via
   `GeoProfiles.NearestTo(lat, lng)`. The result is cached in `_geoMarket`, and a `_geoTried`
   guard prevents re-prompting; denial/timeout is respected (tried once per session).
3. **Ask the user.** If both fail, `_awaitingMarket = true` and the pending query is held
   in `_pendingQuery`. An inline market picker (buttons from `Catalog.Geos`) renders above
   the search bar; choosing one calls `RunPendingSearchAsync(geo)`.

Related components: [`MarketPickerDialog.razor`](../../src/Daleel.Web/Components/Shared/MarketPickerDialog.razor),
[`GeoSelect.razor`](../../src/Daleel.Web/Components/Shared/GeoSelect.razor).

### Kicking off a search

Home injects `IConversationService` and calls:

```csharp
var result = await Conversations.SubmitAsync(_userId!, isAdmin, query, geo, model, lang);
```

[`ConversationService.SubmitAsync`](../../src/Daleel.Web/Conversation/ConversationService.cs)
(1) validates the query, (2) checks the monthly credit balance via `IQuotaService`
(returns HTTP 429 if `!CanSearch`), (3) resolves the model (per-plan default from
`ISystemConfigService` if the caller didn't pick one), (4) inserts a `SearchJob`
(`Status = Queued`, `Geo` defaults to `"jordan"` if null, `Language` defaults to `"en"`),
and (5) enqueues it via `ISearchJobQueue.EnqueueAsync(job.Id)`, returning **202 Accepted**.
Credits are **not** pre-deducted — they are charged after the job finishes, based on the
provider calls actually made. `CancelAsync(userId, jobId)` cancels a running or queued job
(ownership-checked).

### Progress stepper

Results stream in real time via [`SearchProgress.razor`](../../src/Daleel.Web/Components/Shared/SearchProgress.razor):

```razor
<SearchProgress Messages="_progress" Active="true" OnCancel="CancelAsync" />
```

It shows an **8-stage stepper** (each stage a `SearchStep` enum value):

1. Analyzing · 2. Checking Vault · 3. Searching Web · 4. Extracting Products ·
5. Building Profiles · 6. Finding Stores · 7. Comparing Prices · 8. Done

UX details:

- **Desktop/tablet** (≥ sm): horizontal stepper with all 8 stages.
- **Mobile** (< sm): a compact "N of M complete" summary that expands to a vertical list.
- A proportional linear progress bar (0–100%), a current-stage pulse, and an `X of 8 · YY%`
  counter.
- A **live activity feed** of the last ~5 streamed lines; encoded `SearchProgressSignal`
  lines advance the stepper and are localized, while plain lines (agent diagnostics) show
  as-is.

The secondary search pages use the simpler [`ProgressLog.razor`](../../src/Daleel.Web/Components/Shared/ProgressLog.razor)
(a "Researching…/Complete" header plus the last ~20 lines, no stepper).

### Results display

When `_status == "completed"` and an `AgentAnswer` is present, Home renders
[`AdaptiveResults.razor`](../../src/Daleel.Web/Components/Shared/AdaptiveResults.razor),
which **adapts its layout to what came back**:

- **Primary:** [`ProductListings`](../../src/Daleel.Web/Components/Shared/ProductListings.razor)
  (product grid + filter/sort bar).
- **Collapsible panels** (only when present): Stores ([`StoreCards`](../../src/Daleel.Web/Components/Shared/StoreCards.razor)),
  Brands ([`BrandCards`](../../src/Daleel.Web/Components/Shared/BrandCards.razor)), User
  reviews ([`UserReviewList`](../../src/Daleel.Web/Components/Shared/UserReviewList.razor)),
  Related articles ([`ReviewList`](../../src/Daleel.Web/Components/Shared/ReviewList.razor)).
- **Sources:** [`SourceChips`](../../src/Daleel.Web/Components/Shared/SourceChips.razor) at
  the bottom.
- **Enrichment:** after first results arrive, specs are fetched in the background; a
  "Fetching specs…" spinner shows until an `Enriched` event lands or a 60s timeout clears
  `_enriching`.

> **Faulted ≠ empty.** A failed job and a genuinely empty result both surface as a
> "No results" message; the real exception is server-side only. See the project note on the
> faulted-run empty state.

---

## 3. Product cards

### SafeImage

[`SafeImage.razor`](../../src/Daleel.Web/Components/Shared/SafeImage.razor) is the universal
image renderer. **It fails open — images show by default** (`_revealed = true`) and use
`loading="lazy"`. Parameters: `Src` (required), `Alt`, `Class`, `Style`. Null/empty `Src`
renders nothing.

There is an **opt-in privacy blur**: if the browser-stored `img.blur` setting is on,
`SafeImage` extracts the image's domain and checks it against a trusted-domains list in
localStorage. Untrusted domains render a blurred "Image hidden" overlay with two buttons —
**Show image** (reveal once) and **Trust source** (add the domain to the trusted list and
reveal). With blur off (the default), no JS interop runs and the image shows immediately.

> There is no component literally named `ProductCard`. Products are rendered by
> `ModelCard` (aggregated model) and `ShoppingCard` (single listing), both via `SafeImage`.

### ModelCard

[`ModelCard.razor`](../../src/Daleel.Web/Components/Shared/ModelCard.razor) — an aggregated
product *model* (one product, many sellers). Renders a bounded 150px image (fallback icon
if missing), the name (direction-aware), brand · model metadata, a compact
`BrandReputationView` when there's a reputation signal, the lowest price via
`new Money(price, cur).ToDisplay()` with a localized "From", a red **Sale** chip when any
offer `IsDeal`, the seller count, an optional review summary, and up to two pros. Buttons:
**Details** (`OnDetails` callback → detail dialog/page) and **View** (`LowestOffer.Url` in a
new tab). When `ShowCompare` is true, a checkbox drives the `SelectedChanged` callback for
the comparison flow.

### ShoppingCard

[`ShoppingCard.razor`](../../src/Daleel.Web/Components/Shared/ShoppingCard.razor) — a single
deal/listing (`SearchResult`). Renders a direction-aware title, the price via
`Result.Price.ToDisplay()`, a star rating, the seller with a store icon, a **View listing**
button (`Result.Url`, new tab), and a source chip.

### ProductListings (grid + filters)

[`ProductListings.razor`](../../src/Daleel.Web/Components/Shared/ProductListings.razor) wraps
the model grid (MudBlazor grid, `xs=12 sm=6 md=4`, one `ModelCard` per model) with a
filter/sort bar:

- **Brand** (distinct brands from the result), **Source** (marketplace / brand site /
  store), **Condition** (new / used / refurbished).
- **Sort:** relevance (default), price ↑/↓, most sellers.
- **Price range** min/max.

Filtering is LINQ over `Result.Models` (case-insensitive brand match, offer-level source
& condition checks, price bounds), then a `switch` on the sort key. A **sticky comparison
bar** shows the selected count and a **Compare** button (disabled below 2 selections).

---

## 4. Detail pages — data sources

All three detail pages use **stable-ID routing**: the route segment `{Id}` is either a
numeric DB id *or* a stable hash, and the human-readable name rides in the query string
(`?name=...`). This keeps deep links shareable and resolvable even when rows are re-keyed
under a normalized key. **All three read from the database via repositories — not from the
in-memory search JSON.** (Fresh, never-saved results live only in the `AgentAnswer` payload
on the Home page; once a product has been harvested into the catalogue it is reachable by
its detail page.)

### ProductDetail — `/product/{Id}`

[`ProductDetail.razor`](../../src/Daleel.Web/Components/Pages/ProductDetail.razor) loads via
[`IProductDetailDbService.GetAsync`](../../src/Daleel.Web/Services/ProductDetailDbService.cs):

```csharp
_view = await Details.GetAsync(Id, DecodedName,
    string.IsNullOrWhiteSpace(Geo) ? "jordan" : Geo!, lookupKey: Key);
```

The service stitches together three EF entities into a `ProductDetailView`:

1. **`BrandModel`** — harvested catalogue row (specs JSON + R2 image).
2. **`ProductProfile`** — the deep-dive description.
3. **`ScrapedPrice`** time series — latest price per store (offers) + full history (for the
   min/max price range).

Offers are ordered priced-first then cheapest; specs come from `ParseSpecs(SpecsJson)`. The
page renders title/brand/model, image (`SafeImage`), lowest price + seller count, a brand
reputation chip linking to `/brand/{BrandStableId}`, observed price range, last-updated
date, an offers table with **Buy** buttons, the description, and a specs table.

### BrandDetail — `/brand/{Id}`

[`BrandDetail.razor`](../../src/Daleel.Web/Components/Pages/BrandDetail.razor) loads the
`Brand` by numeric id or name (`IBrandRepository`), then its models
(`IBrandModelRepository.ListByBrandAsync`). Renders a logo (DuckDuckGo favicon service, or
fallback icon), origin, a reputation score chip (color by threshold), price range, website,
description, strengths/complaints (`Pros`/`Cons`), and a grid of clickable model cards
(local & global price). Clicking a model navigates to
`/product/{StableId.ForProduct(...)}?name=...`. If no local models exist, it falls back to
`PopularModels` chips.

### StoreDetail — `/store/{Id}`

[`StoreDetail.razor`](../../src/Daleel.Web/Components/Pages/StoreDetail.razor) loads the
`Store` by id or name (`IStoreRepository`) and the latest scraped price per product at that
store (`IScrapedPriceRepository.LatestForStoreAsync`). Renders name + verified badge, type &
rating (prefers `GoogleRating`, falls back to `Rating`), contact info (address, phone, email,
hours), a website button, a keyless embedded Google Maps iframe (prefers lat/lng, falls
back to address), a **products-carried** table (each row links to the product detail page
with `?key={ProductKey}`), and **brands-carried** chips linking to `/brand/...`.

> **Serialization invariant:** [`ResultSerialization`](../../src/Daleel.Web/Services/ResultSerialization.cs)
> centralizes the JSON options (enum-as-string, ignore-nulls) so the *save* path and the
> *view* path never drift.

---

## 5. Comparison

[`Compare.razor`](../../src/Daleel.Web/Components/Pages/Compare.razor) (`/compare`) takes two
products from two text fields (`_a`, `_b`). A rerun-from-history populates them by splitting
the `?Q=` query string on `" vs "`; history stores the query as `"{a} vs {b}"`.

The comparison surface is [`ComparisonTable.razor`](../../src/Daleel.Web/Components/Shared/ComparisonTable.razor)
(also shown in [`ComparisonDialog.razor`](../../src/Daleel.Web/Components/Shared/ComparisonDialog.razor)):

- **Columns = products** (2–4); **rows = specs/attributes** (price, sellers, then fields).
- **Rows are schema-driven when possible.** If a product-type schema exists (phone, AC, …),
  rows use its ordered fields with labels, units, and a `HigherIsBetter` flag; otherwise the
  table unions every spec key across the models and humanizes them (`screen_size` →
  *Screen Size*).
- **Best-value highlighting:** for orderable rows, the leading number in each cell is parsed
  and compared; the winning cell is highlighted when at least two models quote comparable
  numbers. The cheapest column gets a **Best value** chip.

Specs are loaded from the **database** via `IProductDetailDbService.GetComparableAsync`
(returns null → "specs not yet profiled" message). They are sanitized by
[`ProductSpecs.ForDisplay`](../../src/Daleel.Web/Services/ProductSpecs.cs): raw keys
(`details`, `raw`, `content`, `html`, `markdown`, `body`, …) are dropped, as are values over
200 chars, multi-line values, or anything containing links/HTML — only clean key/value
pairs reach the UI.

---

## 6. Admin pages (detail)

### AdminUsage — `/admin/usage`

[Source](../../src/Daleel.Web/Components/Pages/Admin/AdminUsage.razor). Reads `IEventStore`.
Windowed by period (today / week / month / all):

- **KPI cards:** total cost ($), total action count, provider count.
- **Per-provider table:** call count, error count, error-rate %, avg response time, cost.
- **Per-category chips:** event category, calls, cost.
- **Recent activity tail:** last 50 events (`Events.RecentAsync(50)`) — timestamp, status,
  provider, type, search id, duration, cost.

> `IEventStore` has two backends — a SQLite `ApiCallLog` (default) or an optional Postgres
> store. Both are enabled; the startup migration is gated on the `EventStoreDbContext`
> factory.

### AdminWorkflows — `/admin/workflows`

[Source](../../src/Daleel.Web/Components/Pages/Admin/AdminWorkflows.razor). Reads
`DaleelDbContext` (the `SearchJob` table) + `IEventStore`. Implements `IAsyncDisposable`.

- **Running/queued:** query, anonymized user, start, elapsed, current step, with a 5s
  auto-refresh toggle.
- **Completed:** query, status icon (completed/failed/cancelled), finish time, duration,
  result count, cost, step count — sortable, last 200 in the window.
- **Drill-down dialog:** for failed/cancelled runs it shows the **error cause**
  (`job.Error`) and the last step reached (`job.ProgressMessage`), plus a timeline of
  provider calls (`Events.ForSearchAsync(job.Id)`).

> An admin "failed" workflow is typically a genuine **empty-result** failure; the cause now
> surfaces on `SearchJob.Error`.

### AdminData — `/admin/data` (R2 browser)

[Source](../../src/Daleel.Web/Components/Pages/Admin/AdminData.razor). A Cloudflare R2
object-storage browser over `IR2StorageService`. Buckets: `Data`, `Specs`, `Images`,
`Logs`. It lists objects (`ListObjectsAsync(prefix, token, pageSize, bucket)`), separates
folders from files with breadcrumb drill-down and "Load more" pagination, previews images
via a presigned URL (no server pull) and text/JSON via `ReadTextAsync` (pretty-printed,
truncated), and offers downloads via `DownloadUrl(key, bucket)`.

> R2 gotchas (project note): images are no longer uploaded; `StoreJsonAsync` uploads
> regardless of the public host; `AdminData` defaults to the `Logs` bucket.

---

## 7. Settings page

[`Settings.razor`](../../src/Daleel.Web/Components/Pages/Settings.razor) (`/settings`,
`[Authorize]`) was deliberately stripped to exactly **two sections** — there are no theme,
content-filter, or data-preference panels here:

1. **Search history** — a server-paginated `MudTable` of `SearchHistoryEntry` (When, Type,
   Query, Market, Summary, delete). A debounced (300ms) filter field and a **Clear all**
   button (confirmation dialog). Data via
   `ISearchHistoryRepository.ListAsync(userId, search, page, pageSize)`; per-row
   `DeleteAsync`; bulk `ClearAsync`. The repository is **user-isolated** — every method
   takes a `userId` and no method reads across users.
2. **Account deletion** — a single destructive **Delete my account** button. On confirm
   (`Dialogs.ShowMessageBox`) it removes the user's owned rows in order — `SavedResults`,
   `SearchHistory`, `UserSubscriptions`, `UserQuotas`, `ApiCallLogs` — then deletes the
   Identity user via `UserManager.DeleteAsync`, and navigates to `/logout` with
   `forceLoad: true` to clear the auth cookie.

Related: [`History.razor`](../../src/Daleel.Web/Components/Pages/History.razor) is the
full-page history view (adds **Open** → `/?historyId=...` and **Rerun** → the originating
feature page with the query pre-filled). [`Account.razor`](../../src/Daleel.Web/Components/Pages/Account.razor)
hosts the quota/billing dashboard with its own danger-zone delete using the same logic.

---

## 8. Navigation & layout

### MainLayout

[`MainLayout.razor`](../../src/Daleel.Web/Components/Layout/MainLayout.razor) is the app
shell. It renders [`InteractiveProviders`](../../src/Daleel.Web/Components/Layout/InteractiveProviders.razor)
(theme/dialog/snackbar/popover) then wraps everything in `MudRTLProvider`:

```razor
<MudRTLProvider RightToLeft="@RightToLeft">
  <MudLayout>
    <MudAppBar Elevation="1" Dense="true">
      <MudIconButton Icon="@Icons.Material.Filled.Menu" OnClick="ToggleDrawer" />
      <MudLink Href="/"> … دليل · Daleel </MudLink>
      <MudSpacer />
      <AppBarControls />        @* language + theme *@
      <QuotaBadge />
      <UserMenu />
    </MudAppBar>
    <MudDrawer @bind-Open="_drawerOpen"
               Variant="DrawerVariant.Responsive"
               ClipMode="DrawerClipMode.Always">
      <NavMenu />
    </MudDrawer>
    <MudMainContent> … @Body … </MudMainContent>
  </MudLayout>
</MudRTLProvider>
```

**App bar** (left→right): hamburger (toggles drawer), logo/wordmark linking home, spacer,
[`AppBarControls`](../../src/Daleel.Web/Components/Layout/AppBarControls.razor)
(language + theme), [`QuotaBadge`](../../src/Daleel.Web/Components/Layout/QuotaBadge.razor)
(credits), [`UserMenu`](../../src/Daleel.Web/Components/Layout/UserMenu.razor).

**Drawer.** `DrawerVariant.Responsive` auto-collapses on small screens (temporary overlay on
mobile, persistent on desktop); `ClipMode.Always` keeps it clipped below the app bar. Open
state is the local `_drawerOpen` bool toggled by the hamburger.

### NavMenu

[`NavMenu.razor`](../../src/Daleel.Web/Components/Layout/NavMenu.razor):

- Always: **Home**.
- Authenticated only (`AuthorizeView`): **Brand**, then **History**, **Saved**.
- Always: **Pricing**, **FAQ**, **Status**, **Settings**.
- A **mobile-only** account block (`d-md-none`): **Account**, **Admin** (role-gated),
  **Logout** / **Login**. On desktop those live in `UserMenu` instead.

> Deals / Stores / Compare / Monitor are intentionally **not** in the drawer — they're
> reachable by URL but kept out of the nav per product feedback.

### UserMenu

[`UserMenu.razor`](../../src/Daleel.Web/Components/Layout/UserMenu.razor): when authenticated,
a `MudMenu` anchored to an avatar (image from the `avatar_url` claim, else the first initial
of the `display_name` claim / `Identity.Name`) with **Account / History / Saved / Admin
(role) / Logout**. When anonymous, a **Sign in** button → `/login`.

### AuthLayout

[`AuthLayout.razor`](../../src/Daleel.Web/Components/Layout/AuthLayout.razor) is the minimal
static-SSR layout for `/login`, `/register`, `/logout`: its own `MudThemeProvider`, the
wordmark, RTL from culture, and `@Body` — no drawer or interactive menus, so antiforgery
POST forms work.

---

## 9. Localization (EN / AR, RTL)

Supported cultures: **`en`** and **`ar`**, registered in
[`Program.cs`](../../src/Daleel.Web/Program.cs):

```csharp
builder.Services.AddLocalization();
var supportedCultures = new[] { "en", "ar" };
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.SetDefaultCulture("en")
     .AddSupportedCultures(supportedCultures)
     .AddSupportedUICultures(supportedCultures);
    o.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider());
});
```

**Culture resolution order:** culture cookie → `Accept-Language` header → default `en`.

**Switching.** [`LanguageSwitcher.razor`](../../src/Daleel.Web/Components/Shared/LanguageSwitcher.razor)
is an `EN / عربي` button group. Clicking navigates (full reload, `forceLoad: true`) to the
`/set-language` endpoint, which writes the culture cookie (1-year, essential) and
local-redirects back:

```csharp
app.MapGet("/set-language", (string culture, string? redirectUri, HttpContext ctx) =>
{
    var safe = culture is "ar" or "en" ? culture : "en";
    ctx.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(safe)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, Path = "/" });
    var target = string.IsNullOrWhiteSpace(redirectUri) || !redirectUri.StartsWith('/') ? "/" : redirectUri;
    return Results.LocalRedirect(target);
});
```

The full reload is required so request localization re-resolves the culture for the whole
circuit.

**Resource files:** [`Resources/SharedResource.resx`](../../src/Daleel.Web/Resources/SharedResource.resx)
(English) and [`Resources/SharedResource.ar.resx`](../../src/Daleel.Web/Resources/SharedResource.ar.resx)
(Arabic). Components inject `IStringLocalizer<SharedResource> L` and read `L["Key.Path"]`
(e.g. `Nav.Home`, `History.FilterPlaceholder`, `Settings.DeleteDialog.Title`).

**RTL** is wired at three levels:

1. **HTML** — `App.razor` sets `<html lang="@culture.TwoLetterISOLanguageName" dir="@dir">`
   (`rtl` for Arabic).
2. **Layout** — `MudRTLProvider RightToLeft="@RightToLeft"` in `MainLayout`/`AuthLayout`,
   where `RightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft`.
3. **Per-element** — direction-sensitive text (history queries, card titles) uses
   `dir="@Catalog.Dir(text)"`, which detects the text's own language.

The font stack puts **Cairo** first (see §11) so Arabic renders crisply.

---

## 10. SignalR — real-time progress

The mechanism the worker uses to push progress to the browser lives in
[`ConversationHub.cs`](../../src/Daleel.Web/Conversation/ConversationHub.cs). The public class
is **`SignalRConversationBroadcaster`** (there is no class literally named
`ConversationBroadcaster`).

### The hub

```csharp
[Authorize]
public sealed class ConversationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(userId));
        await base.OnConnectedAsync();
    }
    public static string Group(string userId) => $"user:{userId}";
}
```

Mapped in `Program.cs`: `app.MapHub<ConversationHub>("/hubs/conversation");`. Every
connection joins a per-user group `user:{userId}`, so a search started on one device streams
to all of that user's devices.

### Two-tier fan-out

`SignalRConversationBroadcaster` is a **singleton** implementing **both**:

- **`IConversationBroadcaster`** — what the background worker calls.
- **`IConversationNotifier`** — what Blazor Server circuits subscribe to (in-process C#
  events).

Each state change fans **two ways** — to the SignalR group (external clients) *and* to the
in-process notifier (Blazor circuits):

```csharp
public Task ProgressAsync(string userId, int jobId, string message)
{
    _latestProgress[userId] = (jobId, message);
    Progress?.Invoke(userId, jobId, message);                                  // in-process
    return _hub.Clients.Group(ConversationHub.Group(userId))
                       .SendAsync("Progress", jobId, message);                 // SignalR
}
// …Completed → "Completed"; Enriched → "Enriched"
```

### Worker side

[`SearchJobService.cs`](../../src/Daleel.Web/Conversation/SearchJobService.cs) injects
`IConversationBroadcaster` and calls `ProgressAsync` (per stage), `CompletedAsync` (with the
serialized result JSON), and `EnrichedAsync` (post-completion deep-dive specs).

### Browser side

`Home.razor` does **not** open a `HubConnection`. Because the broadcaster is a server
singleton and also an `IConversationNotifier`, every Blazor Server circuit on the instance
receives the same events directly:

```csharp
protected override void OnInitialized()
{
    Notifier.Progress  += OnProgress;
    Notifier.Completed += OnCompleted;
    Notifier.Enriched  += OnEnriched;
}
```

Handlers filter by `userId`/`jobId`, then on `Completed` deserialize the result
(`ResultSerialization.Deserialize<AgentAnswer>`), set `_enriching` if there are listings, and
`StateHasChanged()`. The progress list flows into `SearchProgress`, which decodes each
`SearchProgressSignal` to advance the stepper and localize the line. The raw
`/hubs/conversation` hub is there for **external** clients (mobile / browser extension) that
*do* connect with a `HubConnection`.

---

## 11. Theme — system detection, dark/light, MudBlazor

### The theme

[`DaleelTheme.Build()`](../../src/Daleel.Web/Services/DaleelTheme.cs) is a static factory
returning a `MudTheme` — a deep indigo/teal "intelligence console" dark palette and a clean
light counterpart. Highlights:

- **Dark:** Primary `#5b8def`, Secondary `#22d3ee`, Background `#0b0f19`, Surface `#141a2b`.
- **Light:** Primary `#2f6df6`, Background `#f6f8fc`, Surface `#ffffff`.
- **Layout:** `DefaultBorderRadius = 12px`, `DrawerWidthLeft = 260px`.
- **Typography:** font stack `Cairo, Roboto, Helvetica, Arial, sans-serif` — **Cairo first**
  so Arabic renders crisply; headings use Cairo at weights 600–700.

### State + system detection

Theme state is a scoped service, [`LayoutState`](../../src/Daleel.Web/Services/LayoutState.cs)
(`AddScoped<LayoutState>()`), defaulting to **dark** (`IsDarkMode = true`) and raising a
`Changed` event.

[`InteractiveProviders.razor`](../../src/Daleel.Web/Components/Layout/InteractiveProviders.razor)
mounts the `MudThemeProvider` and adopts the **operating system** preference once the
circuit is live, staying in sync if the OS flips:

```razor
<MudThemeProvider @ref="_provider" Theme="_theme"
                  IsDarkMode="State.IsDarkMode"
                  IsDarkModeChanged="State.SetDarkMode"
                  ObserveSystemThemeChange="true" />
@* OnAfterRenderAsync(firstRender): *@
State.SetDarkMode(await _provider.GetSystemDarkModeAsync());
```

### Manual toggle (a nuance)

The component comments in `InteractiveProviders` describe theme as OS-driven and *"no longer
a stored preference."* However, [`AppBarControls`](../../src/Daleel.Web/Components/Layout/AppBarControls.razor)
**does** expose a manual toggle that also persists to localStorage:

```csharp
private async Task ToggleTheme()
{
    State.ToggleDarkMode();
    await Store.SetAsync("theme", State.IsDarkMode ? "dark" : "light");
}
```

So in practice: the app **starts dark**, **adopts the OS theme** on first interactive
render (and follows live OS changes via `ObserveSystemThemeChange`), and the user can still
**manually flip** dark/light from the app bar (which writes a `theme` value to localStorage).
Because all of these route through the single shared `LayoutState`, toggling anywhere
re-renders the one `MudThemeProvider`. *(The localStorage write and the "not stored"
comment are in mild tension in the current source — flagged here so it isn't mistaken for a
doc error.)*

---

## Component cross-reference

| Concern | Components |
|---------|-----------|
| Search input & flow | `Home`, `SearchProgress`, `ProgressLog`, `MarketPickerDialog`, `GeoSelect`, `AdaptiveResults` |
| Product rendering | `SafeImage`, `ModelCard`, `ShoppingCard`, `ProductListings`, `SourceChips`, `SourceBadge` |
| Detail pages | `ProductDetail`, `BrandDetail`, `StoreDetail`, `BrandReputationView`, `ReviewList`, `UserReviewList` |
| Comparison | `Compare`, `ComparisonTable`, `ComparisonDialog` |
| Save/history | `Saved`, `History`, `Settings`, `SaveResultButton`, `SaveResultDialog`, `SavedResultView` |
| Layout/nav | `MainLayout`, `AuthLayout`, `NavMenu`, `AppBarControls`, `UserMenu`, `QuotaBadge`, `InteractiveProviders` |
| Localization/theme | `LanguageSwitcher`, `DaleelTheme`, `LayoutState` |

---

*Generated from source in `src/Daleel.Web`. When components change, update this page —
especially routes (§1), the progress stages (§2), and the SignalR contract (§10).*
