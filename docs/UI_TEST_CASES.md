# Daleel — UI Test Cases

End-to-end UI test cases for every page in the Daleel Blazor Server web app
(`src/Daleel.Web`). Each case has a stable **ID**, the **page**/route under test, a
**scenario**, concrete **steps**, and the **expected result**.

## Conventions

- **App shell** — MudBlazor on Blazor Server (InteractiveServer). Auth pages
  (`/login`, `/register`, `/logout`) are *static SSR* HTML forms that POST to
  `/auth/*`; everything else runs inside a SignalR circuit.
- **Auth model** — the **first** account ever registered is promoted to **Admin**
  (`AuthEndpoints.cs`, `isFirstUser`). All later accounts are normal users.
- **Localization** — every visible string comes from `IStringLocalizer<SharedResource>`
  (`@L["key"]`). Language is switched by `LanguageSwitcher` which POSTs to
  `/set-language?culture={en|ar}` and force-reloads. Arabic flips the document to
  **RTL** via `MudRtlProvider` / `CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft`.
- **Selectors** — prefer stable `id=` where present (auth inputs:
  `#login-email`, `#login-password`, `#reg-email`, `#reg-password`, `#reg-confirm`),
  otherwise role + accessible name, MudBlazor classes (`.mud-button`, `.mud-table`,
  `.mud-dialog`), or the `daleel-*` CSS hooks (`daleel-stepper`, `daleel-step`,
  `daleel-card`, `daleel-status-pulse`).
- **Priority** — P1 critical path, P2 important, P3 edge/cosmetic.

| Symbol | Meaning |
| --- | --- |
| 🌐 | verify in both EN and AR |
| 🔒 | requires authenticated user |
| 👑 | requires Admin role |

---

## 1. Public pages (unauthenticated)

### 1.1 Landing page — `/` (anonymous) · `LandingPage.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| LAND-01 | Hero renders for anonymous visitor | 1. Open `/` while signed out | Hero title, lede, badge chip (`daleel-landing-hero`), and 5 feature cards (`daleel-feature-card`) render | P1 |
| LAND-02 | "Get Started" CTA | 1. Open `/`<br>2. Click **Get Started** | Navigates to `/register` | P1 |
| LAND-03 | "Sign In" CTA | 1. Click **Sign In** in hero | Navigates to `/login` | P1 |
| LAND-04 | Bottom CTA band links | 1. Scroll to CTA band<br>2. Click **Register** / **Login** | Navigate to `/register` / `/login` respectively | P2 |
| LAND-05 🌐 | Language switch EN→AR | 1. Open `/` in EN<br>2. Click **عربي** in switcher | Page reloads; all hero/feature/CTA text now Arabic; `html[dir="rtl"]` | P1 |
| LAND-06 🌐 | Direction flips to RTL | 1. Switch to AR | `dir="rtl"` applied; layout mirrors (drawer/app-bar on right) | P2 |
| LAND-07 | Switch back AR→EN | 1. In AR, click **EN** | Page reloads in English LTR; `culture` cookie = en | P2 |
| LAND-08 | Authenticated user sees workspace, not landing | 1. Sign in<br>2. Open `/` | Search workspace renders (chat input), not the marketing hero | P2 |

### 1.2 Login — `/login` · `Login.razor` (static SSR + `AuthLayout`)

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| LOGIN-01 | Form renders | 1. Open `/login` | Email (`#login-email`), Password (`#login-password`), Sign-in submit, "Forgot Password?" and "Create Free Account" links, hidden antiforgery token | P1 |
| LOGIN-02 | HTML5 required validation | 1. Submit empty form | Browser blocks submit; `required` fields flagged (no POST) | P2 |
| LOGIN-03 | Successful login redirect | 1. Enter valid email+password<br>2. Submit | 302 to `returnUrl` (default `/`); authed workspace shown; user avatar in app bar | P1 |
| LOGIN-04 | Invalid credentials error | 1. Enter wrong password<br>2. Submit | Redirect to `/login?error=invalid`; MudAlert with generic "invalid" message (no account-existence leak) | P1 |
| LOGIN-05 | Unknown email = same generic error | 1. Enter non-existent email<br>2. Submit | `error=invalid` — identical message to LOGIN-04 | P2 |
| LOGIN-06 | Disabled account | 1. Login as a disabled user | `/login?error=disabled`; "account disabled" alert | P2 |
| LOGIN-07 | Expired/antiforgery failure | 1. Strip antiforgery token, submit | `/login?error=expired`; "session expired, try again" alert (not blank 400) | P3 |
| LOGIN-08 | returnUrl preserved | 1. Open `/login?returnUrl=/history`<br>2. Login | After success lands on `/history` | P2 |
| LOGIN-09 | Open-redirect rejected | 1. `/login?returnUrl=https://evil.com`<br>2. Login | Redirect coerced to `/` (SafeLocalPath) | P2 |
| LOGIN-10 🌐 | Arabic translations | 1. Switch to AR, open `/login` | Labels, placeholders, submit, links all Arabic; RTL | P2 |
| LOGIN-11 | Already-signed-in view | 1. While authed open `/login` | AuthorizeView shows "already signed in" + link home, not the form | P3 |
| LOGIN-12 | Links navigate | 1. Click "Forgot Password?" / "Create Free Account" | Go to `/forgot-password` / `/register` | P3 |

### 1.3 Register — `/register` · `Register.razor` (static SSR + `AuthLayout`)

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| REG-01 | Form renders | 1. Open `/register` | Email (`#reg-email`), Password (`#reg-password`), Confirm (`#reg-confirm`), password hint caption, Create-account submit, antiforgery token | P1 |
| REG-02 | Missing fields | 1. Submit empty | HTML5 `required` blocks; if bypassed → `error=missing` | P2 |
| REG-03 | Password mismatch | 1. Password ≠ Confirm<br>2. Submit | `/register?error=mismatch`; mismatch alert | P1 |
| REG-04 | Weak password | 1. Enter `123`<br>2. Submit | `/register?error=weakpassword`; password-requirement alert | P1 |
| REG-05 | Duplicate email | 1. Register existing email | `/register?error=duplicate`; duplicate alert | P1 |
| REG-06 | Successful registration auto-signs-in | 1. Enter unique email + valid matching passwords<br>2. Submit | 302 to `returnUrl`; user is signed in (cookie set) | P1 |
| REG-07 | First user becomes Admin | 1. On a fresh DB, register the first account | User gets Admin role; `/admin` reachable; UserMenu shows Admin link | P1 |
| REG-08 | Second user is NOT admin | 1. Register a second account | No Admin role; `/admin` blocked/redirected | P2 |
| REG-09 🌐 | Arabic translations | 1. AR, open `/register` | All labels/hint/links Arabic; RTL | P2 |
| REG-10 | "Learn more" / "Sign In" links | 1. Click each | Go to `/faq` / `/login` | P3 |

### 1.4 FAQ — `/faq` · `Faq.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| FAQ-01 | Page renders | 1. Open `/faq` | Title, subtitle, 7 expansion panels (Q1–Q7) | P1 |
| FAQ-02 | Accordion expand | 1. Click a panel header | Panel expands; answer text visible | P1 |
| FAQ-03 | Multi-expansion | 1. Expand Q1 then Q2 | Both stay open (`MultiExpansion="true"`) | P2 |
| FAQ-04 | Collapse | 1. Click an expanded header | Panel collapses; answer hidden | P2 |
| FAQ-05 🌐 | Arabic | 1. AR, open `/faq` | All Q/A Arabic; RTL | P2 |

### 1.5 Pricing — `/pricing` · `Pricing.razor` + `PricingTiers.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| PRICE-01 | Page renders | 1. Open `/pricing` | Title, subtitle, plan cards via `PricingTiers` | P1 |
| PRICE-02 | Plan cards + features | 1. Inspect each card | Name, price/month, feature bullet list per active plan | P1 |
| PRICE-03 | Plans reflect admin config | 1. Admin edits a plan<br>2. Reopen `/pricing` | New name/price/features shown | P2 |
| PRICE-04 | Billing notice | 1. View page | "Paid plans not yet billable / Stripe coming" notice present | P3 |
| PRICE-05 🌐 | Arabic | 1. AR | Title/subtitle Arabic; RTL | P2 |

---

## 2. Authenticated pages 🔒

### 2.1 Home / Search — `/` (authed) · `Home.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| HOME-01 | Idle workspace renders | 1. Sign in, open `/` | Logo + tagline hero, sticky chat input (`MudTextField` + send `MudIconButton`), recent-history sidebar | P1 |
| HOME-02 | Submit a search | 1. Type a query<br>2. Click send (or Enter) | Query echoes in a `MudPaper` with person icon; input disabled while `_running` | P1 |
| HOME-03 | Progress animation appears | 1. Submit a search | `SearchProgress` renders: pulse dot (`daleel-status-pulse`), horizontal stepper (`daleel-stepper`, 8 steps), progress bar, activity feed | P1 |
| HOME-04 | Stepper advances | 1. Watch a running search | Steps move `pending`→`active`→`done`; progress bar value increases | P2 |
| HOME-05 | Cancel a search | 1. While running click Cancel | Search stops; cancelled state message shown | P2 |
| HOME-06 | Results render (products) | 1. Let a product search finish | `AdaptiveResults` → product/model cards with image (`SafeImage`), price, sellers | P1 |
| HOME-07 | Brand cards | 1. Search surfaces brands | `BrandCards` render with logos/reputation | P2 |
| HOME-08 | Store cards | 1. Search surfaces stores | `StoreCards` render | P2 |
| HOME-09 | Empty / no-results state | 1. Search a nonsense term | Friendly empty-state message, no crash | P2 |
| HOME-10 | Error state | 1. Force a backend error | Centered error `MudPaper` with icon + localized message | P3 |
| HOME-11 | Recent history sidebar | 1. After ≥1 search, reload `/` | "Recent" list populated; **View All** → `/history` | P2 |
| HOME-12 | Re-run from sidebar | 1. Click a recent item | Query prefilled / results reloaded | P2 |
| HOME-13 | Deep link `?q=` | 1. Open `/?q=iphone%2015` | Search auto-runs for that query | P2 |
| HOME-14 | Deep link `?historyId=` | 1. Open `/?historyId={id}` | Saved results for that history entry load | P2 |
| HOME-15 | Send disabled when empty | 1. Empty input | Send button disabled | P3 |
| HOME-16 🌐 | Arabic search UI | 1. AR | Placeholder/tagline Arabic; RTL; Arabic query renders RTL (`Catalog.Dir`) | P2 |

### 2.2 Search results & filtering · `ProductListings.razor` / `AdaptiveResults.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| RES-01 | Filter bar renders | 1. Get product results | Brand, Source, Condition, Sort selects + Min/Max price numeric fields + "N of M shown" caption | P1 |
| RES-02 | Filter by brand | 1. Select a brand | Grid narrows to that brand; count updates | P1 |
| RES-03 | Filter by source | 1. Select Marketplaces / Brand sites / Stores | Grid filtered accordingly | P2 |
| RES-04 | Filter by condition | 1. Select New / Used / Refurbished | Grid filtered | P2 |
| RES-05 | Min/Max price | 1. Set min & max | Out-of-range items removed; count updates | P2 |
| RES-06 | Sort | 1. Choose Price ↑ / Price ↓ / Most sellers | Cards reorder | P2 |
| RES-07 | Compare selection (2–4) | 1. Tick 2 cards | Sticky compare footer shows "2 selected"; Compare enabled | P1 |
| RES-08 | Compare disabled < 2 | 1. Tick only 1 | Compare button disabled | P2 |
| RES-09 | Open comparison dialog | 1. Tick 2, click **Compare** | `ComparisonDialog` opens with `ComparisonTable` | P1 |
| RES-10 | Clear comparison | 1. Click **Clear** in footer | Selections cleared; footer hides | P2 |
| RES-11 | Detail popup opens | 1. Click **Details** on a card | `ModelDetailDialog` opens with price table | P1 |
| RES-12 | Browse-externally links | 1. Click a marketplace link | Opens marketplace in new tab (`target=_blank`) | P3 |
| RES-13 | Grouped comparisons | 1. Results include pre-grouped sets | Expansion panels with category + recommendation render | P3 |

### 2.3 Product detail popup · `ModelDetailDialog.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| PDLG-01 | Concise view | 1. Open dialog | Name, brand·model, image/MSRP, "Available at N places" offer table (source, price, tags LOWEST/SALE/FREE SHIPPING, condition, Buy) | P1 |
| PDLG-02 | Pros/Cons + reputation | 1. Scroll dialog | `BrandReputationView`, review summary, Pros/Cons columns when data present | P2 |
| PDLG-03 | Load full specs | 1. Click **Load full specs & reviews** | Spinner; specs/reviews populate (`DeepScrape`) | P2 |
| PDLG-04 | View Full Details link | 1. Click **View Full Details** | Navigates to `/product/{id}?name=…&geo=…` | P1 |
| PDLG-05 | Close | 1. Click **Close** | Dialog dismisses; underlying results intact | P2 |
| PDLG-06 | Buy opens seller | 1. Click a **Buy** | Seller URL opens in new tab | P3 |

### 2.4 Product full page — `/product/{id}` · `ProductDetail.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| PROD-01 | Direct URL loads | 1. Open `/product/{id}?name=iPhone%2015` | "Deep-scanning…" spinner, then product header/price | P1 |
| PROD-02 | Where-to-buy table | 1. Wait for load | `MudSimpleTable`: source badge, price, condition chip, Buy button per seller | P1 |
| PROD-03 | Price summary | 1. View header | Lowest price + seller count; MSRP chip when known | P2 |
| PROD-04 | Specs table | 1. Scroll | Key/value specifications table | P1 |
| PROD-05 | Pros/Cons | 1. Scroll | Pros (ThumbUp) / Cons (ThumbDown) lists when present | P2 |
| PROD-06 | Brand reputation | 1. View | `BrandReputationView` if a reputation signal exists | P2 |
| PROD-07 | Image / placeholder | 1. View | `SafeImage` shows; placeholder icon when image missing/broken | P2 |
| PROD-08 | Missing `name` param | 1. Open `/product/{id}` with no `?name=` | Graceful error alert (can't re-scan) | P3 |
| PROD-09 | Back to search | 1. Click **Back to search** | Navigates to `/` | P3 |
| PROD-10 | Unknown id | 1. Open `/product/doesnotexist` | Error alert, no crash | P2 |

### 2.5 Brand full page — `/brand/{id}` · `BrandDetail.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| BRD-01 | Direct URL loads | 1. Open `/brand/{id}?name=Samsung` | Spinner then brand header (logo/favicon, name, country) | P1 |
| BRD-02 | Reputation + price range chips | 1. View | Reputation score (star) + price-range chips | P1 |
| BRD-03 | Description | 1. View | Bidirectional brand description text | P2 |
| BRD-04 | Strengths/complaints | 1. Scroll | Pros/Cons grid when present | P2 |
| BRD-05 | Models list | 1. Scroll | Model cards (image, name, local price) or popular-model chips | P1 |
| BRD-06 | Official website link | 1. Click website button | Opens brand site in new tab | P2 |
| BRD-07 | Back button | 1. Click back | Navigates to `/` | P3 |
| BRD-08 | Unknown brand id | 1. Open bad id | Error alert | P2 |

> Note: `/brand` (no id, `Brand.razor`) is the interactive brand-analysis page —
> input + GeoSelect + ModelSelect + **Analyze**, ProgressLog, results (stores,
> competitors, sentiment). Covered by analysis-flow cases BANA-01..05 in §2.11.

### 2.6 Store full page — `/store/{id}` · `StoreDetail.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| STORE-01 | Direct URL loads | 1. Open `/store/{id}?name=Extra` | Spinner then store header (storefront icon, name, verified badge) | P1 |
| STORE-02 | Rating + type | 1. View | Read-only `MudRating` + review count + type chip | P2 |
| STORE-03 | Contact info | 1. View | Address, phone (`tel:`), email (`mailto:`), opening hours rows | P1 |
| STORE-04 | Website button | 1. Click website | Opens store site in new tab | P2 |
| STORE-05 | Google Maps link | 1. Click **Maps** | Opens Google Maps URL (`MapUrl`) | P1 |
| STORE-06 | Brands carried | 1. Click a brand chip | Navigates to `/brand/{stableId}?name=…` | P2 |
| STORE-07 | Map preview | 1. View | Map placeholder/preview area renders | P3 |
| STORE-08 | Unknown store id | 1. Bad id | Error alert | P2 |

### 2.7 Compare — `/compare` · `Compare.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| CMP-01 | Page renders | 1. Open `/compare` | Product A field, Product B field, **VS** chip, GeoSelect, ModelSelect, **Compare** button | P1 |
| CMP-02 | Compare disabled until both filled | 1. Fill only A | Compare button disabled | P2 |
| CMP-03 | Run compare | 1. Fill A & B<br>2. Click Compare | ProgressLog runs; results render | P1 |
| CMP-04 | Side-by-side specs | 1. View results | `ComparisonTable` spec-by-spec; two product breakdown cards | P1 |
| CMP-05 | Best-value / winner highlight | 1. View | Winner chip (success) on the better product; best cells highlighted | P1 |
| CMP-06 | N/A handling | 1. Compare items with missing specs | Missing values shown as N/A / "—", no blank rows or crash | P1 |
| CMP-07 | Pros/Cons per product | 1. View | "＋" pros / "－" cons lists per card | P2 |
| CMP-08 | Verdict | 1. Scroll | `ReportView` verdict text + `SourceChips` | P2 |
| CMP-09 | Deep link `?q=A vs B` | 1. Open `/compare?q=iPhone%2015%20vs%20Galaxy%20S24` | Fields prefilled, may auto-run | P2 |
| CMP-10 | Save result | 1. Click `SaveResultButton` | Comparison saved to `/saved` | P2 |

### 2.8 History — `/history` · `History.razor` 🔒

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| HIST-01 | Anonymous redirect/prompt | 1. Signed out open `/history` | Redirect to login (or sign-in alert with login button) | P1 |
| HIST-02 | Past searches list | 1. Authed open `/history` | `MudTable`: When, Type, Query, Market, Summary, Actions | P1 |
| HIST-03 | Filter searches | 1. Type in filter field | Table narrows (debounced) | P2 |
| HIST-04 | Open a search | 1. Click a query link | Loads `/?historyId={id}` with saved results | P1 |
| HIST-05 | Re-run (replay) | 1. Click replay icon | Reruns on originating page (`/brand?q=…`, `/stores?q=…`, etc.) | P1 |
| HIST-06 | Delete one | 1. Click delete icon | Row removed | P2 |
| HIST-07 | Clear all | 1. Click **Clear All** | All history removed; empty-state shown | P2 |
| HIST-08 | Pagination | 1. With >10 rows change page size 10/20/50 | Pager works | P2 |
| HIST-09 | Empty state | 1. New user, no history | NoRecords empty text | P2 |

### 2.9 Saved results — `/saved` · `Saved.razor` 🔒

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| SAVE-01 | Anonymous prompt | 1. Signed out open `/saved` | Sign-in alert + login button | P1 |
| SAVE-02 | Saved items grid | 1. Authed with saved items | `daleel-card` grid: type chip, timestamp, title, notes | P1 |
| SAVE-03 | Empty state | 1. No saved items | "Nothing saved yet" alert | P2 |
| SAVE-04 | View item | 1. Click **View** | `SavedResultDialogHost` dialog renders saved result | P1 |
| SAVE-05 | Delete item | 1. Click delete | Card removed | P2 |
| SAVE-06 | Export one (JSON) | 1. Click JSON export on a card | JSON file downloads | P3 |
| SAVE-07 | Export all (JSON) | 1. Click **Export all** | Combined JSON downloads (when enabled) | P3 |
| SAVE-08 | Save → appears here | 1. Save from search<br>2. Open `/saved` | New card present | P2 |

### 2.10 Deals — `/deals` · `Deals.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| DEAL-01 | Page renders | 1. Open `/deals` | Product field, GeoSelect, ModelSelect, **Find Deals** button | P1 |
| DEAL-02 | Run | 1. Enter product<br>2. Find Deals | ProgressLog; best-deals `ReportView` + shopping cards | P1 |
| DEAL-03 | Sort dropdown | 1. Change PriceAsc/PriceDesc/Rating/Relevance | Shopping cards reorder | P2 |
| DEAL-04 | No-price info alert | 1. Search yields no priced listings | Info alert shown | P3 |
| DEAL-05 | Save / sources | 1. SaveResultButton; SourceChips | Save works; sources listed | P3 |
| DEAL-06 | Enter submits | 1. Press Enter in field | Triggers Run | P3 |

### 2.11 Stores — `/stores` · `Stores.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| STR-01 | Page renders | 1. Open `/stores` | Product field, GeoSelect, **Find** button, map preview placeholder, sort dropdown | P1 |
| STR-02 | Run | 1. Enter term, Find | ProgressLog; store count + StoreCards | P1 |
| STR-03 | Use my location | 1. Click **Use My Location**, allow geo | Coords captured; "clear" appears | P2 |
| STR-04 | Sort | 1. Change Rating/Distance/Reviews | Cards reorder | P2 |
| STR-05 | No-stores info | 1. Term yields none | Info alert ("requires Google Places API") | P3 |
| STR-06 | Card → store detail | 1. Click a store card | Navigates to `/store/{id}` | P2 |

### 2.11b Brand analysis — `/brand` (interactive) · `Brand.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| BANA-01 | Page renders | 1. Open `/brand` | Brand field, GeoSelect, ModelSelect, **Analyze** | P1 |
| BANA-02 | Analyze disabled when empty | 1. Empty field | Analyze disabled | P2 |
| BANA-03 | Run analysis | 1. Enter brand, Analyze | ProviderStatusBar + ProgressLog; report renders | P1 |
| BANA-04 | Results sections | 1. View | ReportView, SentimentView, store/location cards, competitor chips, SourceChips | P2 |
| BANA-05 | Save analysis | 1. SaveResultButton | Saved to `/saved` | P3 |

### 2.12 Monitor — `/monitor` · `Monitor.razor` 🔒

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| MON-01 | Anonymous redirect | 1. Signed out open `/monitor` | Redirect to login | P1 |
| MON-02 | Page renders | 1. Authed open `/monitor` | New-monitor panel (keyword, GeoSelect, interval select, **Add Monitor**) + active-monitors + results feed | P1 |
| MON-03 | Add monitor | 1. Keyword + interval<br>2. Add | New monitor card appears (status, geo, interval, match chips) | P1 |
| MON-04 | Add disabled when empty | 1. Empty keyword | Add disabled | P2 |
| MON-05 | Run now | 1. Click refresh on a card | Runs immediately; results feed updates | P2 |
| MON-06 | Pause / resume | 1. Toggle pause | Icon + status flip (PlayCircle ↔ PauseCircle) | P2 |
| MON-07 | Delete monitor | 1. Click delete | Card removed | P2 |
| MON-08 | Results feed | 1. After a run | `MudTimeline` hits: text, keyword chip, source, timestamp, optional open link | P2 |

### 2.13 Account / Settings — `/account`, `/settings`, `/logout`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ACC-01 | Account page | 1. Open `/account` | Profile/account details render | P2 |
| ACC-02 | Settings page | 1. Open `/settings` | User settings render | P2 |
| ACC-03 | Logout | 1. UserMenu → Logout (`/logout`) → submit | Auth cookie cleared; redirected to `/` as anonymous | P1 |
| ACC-04 | Forgot password | 1. Open `/forgot-password` | Reset-request form renders | P3 |

---

## 3. App shell / navigation

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| NAV-01 | App bar renders | 1. Any page | Menu toggle, "دليل · Daleel" brand link, LanguageSwitcher, theme toggle, QuotaBadge, UserMenu | P1 |
| NAV-02 | Drawer toggle | 1. Click menu icon | `MudDrawer` opens/closes | P2 |
| NAV-03 | NavMenu (anonymous) | 1. Signed out | Home, Pricing, FAQ, Status, Settings; Brand/History/Saved hidden | P2 |
| NAV-04 | NavMenu (authed) | 1. Signed in | Brand, History, Saved now visible | P2 |
| NAV-05 | UserMenu (authed) | 1. Click avatar | Account, History, Saved, Logout; **Admin** only if Admin role | P1 |
| NAV-06 | UserMenu (anonymous) | 1. Signed out | Sign-in button instead of avatar | P2 |
| NAV-07 | Theme toggle | 1. Click light/dark | Theme flips; persisted to BrowserStore | P2 |
| NAV-08 | Quota badge | 1. Authed | Shows remaining search quota | P3 |
| NAV-09 | Footer links | 1. Click Privacy/Pricing/FAQ/Status | Navigate correctly | P3 |
| NAV-10 🌐 | RTL mirrors shell | 1. Switch AR | Drawer/app-bar mirror; `dir=rtl` | P2 |

---

## 4. Admin pages 👑 (`[Authorize(Roles="Admin")]`)

### 4.0 Admin access control

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-AC-01 | Non-admin blocked | 1. As normal user open `/admin` | Redirected to login/forbidden; no dashboard | P1 |
| ADM-AC-02 | Anonymous blocked | 1. Signed out open `/admin/users` | Redirect to login | P1 |
| ADM-AC-03 | AdminNav tab strip | 1. As admin open `/admin` | 10 tabs: Dashboard, Users, Plans, Analytics, Usage, Moderation, Filtered, Brands, Stores, Settings | P2 |

### 4.1 Admin dashboard — `/admin` · `AdminDashboard.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-DASH-01 | KPI cards render | 1. Open `/admin` | 4 KPI cards: Users total (+today), New this week (+month), Searches today (+week), Searches/month | P1 |
| ADM-DASH-02 | Active subscriptions | 1. View | Plan→count breakdown panel | P2 |
| ADM-DASH-03 | Top queries (month) | 1. View | Query→count list (RTL-aware) | P2 |
| ADM-DASH-04 | Loading state | 1. On open | Indeterminate `MudProgressLinear` before data | P3 |
| ADM-DASH-05 | Error + retry | 1. Force load error | Error alert + **Retry** reloads | P2 |

### 4.2 Admin users — `/admin/users` · `AdminUsers.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-USR-01 | User list | 1. Open `/admin/users` | `MudTable`: User, Plan, Used, Joined, Last active, Roles/State, Actions | P1 |
| ADM-USR-02 | Search/filter | 1. Type name/email | Table filters live | P2 |
| ADM-USR-03 | Disable — confirm dialog | 1. Click disable (Block icon) | MessageBox "Disable account?" with **Disable**/**Cancel** | P1 |
| ADM-USR-04 | Disable — confirm | 1. Click **Disable** | Account disabled; "disabled" chip; snackbar "Account disabled (signed out everywhere)." | P1 |
| ADM-USR-05 | Disable — cancel | 1. Open dialog → **Cancel** | No change | P2 |
| ADM-USR-06 | Enable | 1. Disabled user → LockOpen → confirm | Re-enabled; snackbar "Account enabled." | P2 |
| ADM-USR-07 | Grant admin — confirm | 1. Click admin icon → **Grant admin** | "admin" chip; snackbar "Granted admin (effective on next sign-in)." | P1 |
| ADM-USR-08 | Revoke admin — confirm | 1. Admin user → **Revoke admin** | Chip removed; snackbar "Revoked admin (signed out everywhere)." | P2 |
| ADM-USR-09 | Change plan — confirm | 1. Pick a plan in row select → **Change plan** | "Change subscription plan?" dialog; on confirm snackbar "Plan updated." | P1 |
| ADM-USR-10 | Change plan — cancel | 1. Pick plan → **Cancel** | Select reverts; no change | P2 |
| ADM-USR-11 | Pagination | 1. Change page size 25/50/100 | Pager works | P3 |
| ADM-USR-12 | Action error | 1. Force update failure | Error snackbar "Couldn't …: …"; state unchanged | P3 |

### 4.3 Admin plans — `/admin/plans` · `AdminPlans.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-PLN-01 | Plan list | 1. Open `/admin/plans` | Card per plan (Name, Credits/mo, Price, Sort, Active switch, Features) | P1 |
| ADM-PLN-02 | Add plan | 1. Click **New plan** | New blank editable card | P2 |
| ADM-PLN-03 | Edit features — add | 1. Click **Add feature**, type text | New feature row | P1 |
| ADM-PLN-04 | Edit features — remove | 1. Click feature ✕ | Feature removed | P2 |
| ADM-PLN-05 | Save | 1. Edit, click **Save** | Snackbar "Saved {name}."; persists on reload | P1 |
| ADM-PLN-06 | Save without name | 1. Clear name, Save | Warning "Plan name is required." | P2 |
| ADM-PLN-07 | Delete — confirm dialog | 1. Click **Delete** | "Delete plan?" dialog, **Delete**/**Cancel** | P1 |
| ADM-PLN-08 | Delete — confirm | 1. Click **Delete** | Plan removed; snackbar "Plan removed." | P1 |
| ADM-PLN-09 | Delete — cancel | 1. Cancel dialog | Plan stays | P2 |
| ADM-PLN-10 | Active toggle | 1. Toggle Active off, save | Plan hidden from public `/pricing` | P2 |

### 4.4 Admin analytics — `/admin/analytics` · `AdminAnalytics.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-ANL-01 | Charts render | 1. Open `/admin/analytics` | Line chart "Searches per day (14)"; bar chart "Cost per day" | P1 |
| ADM-ANL-02 | By search type | 1. View | Type→count panel (30d) | P2 |
| ADM-ANL-03 | Top markets | 1. View | Geo→count panel (30d) | P2 |
| ADM-ANL-04 | Cost KPIs | 1. View | Total API cost + Avg cost/search cards | P1 |
| ADM-ANL-05 | Provider usage table | 1. View | Provider, Calls, Cost, Avg time, Error rate (red if >10%) | P1 |
| ADM-ANL-06 | LLM token table | 1. View | Model, Input/Output tokens, Cost | P2 |
| ADM-ANL-07 | Most expensive searches | 1. View | Query, Cost, Call count list | P2 |
| ADM-ANL-08 | Empty states | 1. Fresh instance | "No … yet" placeholders, no crash | P2 |

### 4.5 Admin usage — `/admin/usage` · `AdminUsage.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-USE-01 | KPI cards | 1. Open `/admin/usage` | Cost, Actions, Providers KPI cards | P1 |
| ADM-USE-02 | Not-configured gate | 1. No event store (Postgres) | Info alert "Not configured…"; no tables | P1 |
| ADM-USE-03 | Provider table | 1. With data | `MudDataGrid`: Provider, Calls, Errors, Error %, Avg ms, Cost | P1 |
| ADM-USE-04 | Provider table sortable | 1. Click a column header | Single-column sort toggles | P2 |
| ADM-USE-05 | Category chips | 1. View | Outlined chips: category + count + cost | P2 |
| ADM-USE-06 | Recent events table | 1. View | `MudDataGrid`: timestamp, status icon, provider, event-type chip, search id (8-char mono), duration, cost; "latest 50" | P1 |
| ADM-USE-07 | Period toggle | 1. Switch today/week/month/all | KPIs + tables refresh; spinner during load | P1 |
| ADM-USE-08 | Error % colour | 1. Provider with >10% errors | Error rate shown in red | P3 |
| ADM-USE-09 🌐 | Localized | 1. AR | Title/subtitle Arabic | P2 |

### 4.6 Admin brands — `/admin/brands` · `AdminBrands.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-BRD-01 | Brand list | 1. Open `/admin/brands` | Count caption + table: Brand, Origin, Reputation, Price range, Popular models, Refreshed, Actions | P1 |
| ADM-BRD-02 | Empty state | 1. No profiles | Info alert "No brand profiles yet…" | P2 |
| ADM-BRD-03 | Refresh one | 1. Click row **Refresh** | Status "Refreshing {name}…" → snackbar "Refreshed {name}." | P1 |
| ADM-BRD-04 | Refresh stale | 1. Click **Refresh stale** | Batch refresh; snackbar "Refreshed {n} stale profile(s)." | P2 |
| ADM-BRD-05 | Research unavailable | 1. Refresh with no API keys | Warning "Research unavailable — set CONTEXT_DEV_API_KEY + an LLM key." | P3 |

### 4.7 Admin stores — `/admin/stores` · `AdminStores.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-STO-01 | Store list | 1. Open `/admin/stores` | Count caption + table: Store, Location, Type, Rating, Brands carried, Refreshed, Actions | P1 |
| ADM-STO-02 | Empty state | 1. No profiles | Info alert "No store profiles yet…" | P2 |
| ADM-STO-03 | Refresh one | 1. Click **Refresh** | Status then snackbar "Refreshed {name}." | P1 |
| ADM-STO-04 | Refresh stale | 1. Click **Refresh stale** | Batch refresh snackbar | P2 |

### 4.8 Admin settings — `/admin/settings` · `AdminSettings.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-SET-01 | Settings list | 1. Open `/admin/settings` | Rows: key (mono) + value editor + type chip | P1 |
| ADM-SET-02 | Bool switch | 1. Toggle a `bool` setting | Switch flips true/false | P2 |
| ADM-SET-03 | Int validation | 1. Put text in an `int` setting, Save | Warning "{key} must be a whole number." | P1 |
| ADM-SET-04 | Bool validation | 1. Invalid bool value, Save | Warning "{key} must be true or false." | P2 |
| ADM-SET-05 | Save all | 1. Edit valid values, **Save all** | Button shows "Saving…"; snackbar "Settings saved."; persists | P1 |
| ADM-SET-06 | Save error | 1. Force failure | Error snackbar "Couldn't save all settings: …" | P3 |

### 4.9 Admin filtered — `/admin/filtered` · `AdminFiltered.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-FIL-01 | Filtered log | 1. Open `/admin/filtered` | Count caption + table: When, Category chip, Rule (code), Kind, Query, Filtered content | P1 |
| ADM-FIL-02 | Empty state | 1. Nothing filtered | "Nothing has been filtered yet." | P2 |
| ADM-FIL-03 | RTL content | 1. Arabic query/content rows | Rendered RTL with ellipsis when long | P3 |

### 4.10 Admin moderation — `/admin/moderation` · `AdminModeration.razor`

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| ADM-MOD-01 | KPI cards | 1. Open `/admin/moderation` | "Items filtered (30d)" + "Searches with removals" | P1 |
| ADM-MOD-02 | Categories | 1. View | "Most filtered categories (30d)" category→count | P2 |
| ADM-MOD-03 | Link to filtered | 1. Click footer link | Navigates to `/admin/filtered` | P3 |
| ADM-MOD-04 | Empty state | 1. Nothing filtered | "Nothing has been filtered yet." | P2 |

---

## 5. Cross-cutting

| ID | Scenario | Steps | Expected result | Pri |
| --- | --- | --- | --- | --- |
| X-01 🌐 | Language toggle switches ALL visible text | 1. On each major page toggle EN↔AR | Every label/button updates; no leftover English in AR (and vice-versa) | P1 |
| X-02 | Blazor circuit reconnect | 1. Briefly drop network, restore | Reconnect overlay shows then clears; page interactive again | P2 |
| X-03 | Deep-link auth gate | 1. Signed out open any `[Authorize]` route | Redirect to `/login?returnUrl=…`; after login land on target | P1 |
| X-04 | 404 / error page | 1. Open an unknown route | `Error.razor` / not-found renders gracefully | P3 |
| X-05 | Responsive layout | 1. Resize to mobile width | Grid collapses (xs); drawer becomes overlay; no horizontal scroll | P2 |
| X-06 | Haram blur banner | 1. Trigger filtered content | `HaramBlurBanner` alert shows | P3 |
| X-07 | Status / Diagnostics | 1. Open `/status`, `/diagnostics` | Health/diagnostics render | P3 |

---

### Coverage summary

| Area | Pages | Cases |
| --- | --- | --- |
| Public | Landing, Login, Register, FAQ, Pricing | 40 |
| Authenticated | Home/Search, Results, Product (popup+page), Brand, Store, Compare, History, Saved, Deals, Stores, Brand-analysis, Monitor, Account/Settings | 90+ |
| App shell | App bar, drawer, nav, user menu, theme, RTL | 10 |
| Admin | Dashboard, Users, Plans, Analytics, Usage, Brands, Stores, Settings, Filtered, Moderation + access control | 55+ |
| Cross-cutting | i18n, reconnect, auth gate, errors, responsive | 7 |

The **P1 critical paths** are automated as Playwright E2E tests in
`tests/Daleel.E2E.Tests` — see that project's `README.md` for the mapping.
