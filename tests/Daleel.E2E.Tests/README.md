# Daleel.E2E.Tests

Playwright end-to-end browser tests for the Daleel Blazor Server web app
(`src/Daleel.Web`). They drive a **real Chromium browser** against a **running**
instance of the app and verify the critical user journeys documented in
[`docs/UI_TEST_CASES.md`](../../docs/UI_TEST_CASES.md).

## How it runs (and why it's safe in CI)

These tests need a live server. The suite reads the target from the
`E2E_BASE_URL` environment variable:

- **`E2E_BASE_URL` unset** → every test `Assert.Ignore`s. The project still
  **compiles and the test run stays green** (skipped), so CI that only does
  `dotnet build` / `dotnet test` without a server never goes red.
- **`E2E_BASE_URL` set** → tests execute against that URL.

## Running locally

1. Start the app (uses local SQLite, no external services required):

   ```bash
   dotnet run --project src/Daleel.Web
   # note the printed HTTPS URL, e.g. https://localhost:7120
   ```

2. Install the Playwright browsers once (after the first `dotnet build`):

   ```bash
   pwsh tests/Daleel.E2E.Tests/bin/Debug/net8.0/playwright.ps1 install chromium
   # or:  dotnet tool install --global Microsoft.Playwright.CLI && playwright install chromium
   ```

3. Point the tests at the running app and run them:

   ```bash
   E2E_BASE_URL=https://localhost:7120 dotnet test tests/Daleel.E2E.Tests
   ```

   For a brand-new (empty) SQLite DB, the **first** account the suite registers
   becomes the **admin** — that's what the admin tests rely on. To run against a
   DB that already has users, supply explicit admin credentials instead:

   ```bash
   E2E_BASE_URL=https://localhost:7120 \
   E2E_ADMIN_EMAIL=admin@example.com E2E_ADMIN_PASSWORD='…' \
   dotnet test tests/Daleel.E2E.Tests
   ```

### Configuration knobs (environment variables)

| Variable | Default | Purpose |
| --- | --- | --- |
| `E2E_BASE_URL` | _unset_ (tests skip) | Base URL of the running app |
| `E2E_HEADED` | `0` | `1` to watch the browser |
| `E2E_SLOWMO` | `0` | ms delay between actions (debugging) |
| `E2E_TIMEOUT_MS` | `30000` | default action/navigation timeout |
| `E2E_ADMIN_EMAIL` / `E2E_ADMIN_PASSWORD` | _unset_ | use a pre-seeded admin instead of first-user-is-admin |

## Design

- **Page Object Model** — `Pages/` wraps each page/feature behind
  intent-revealing locators and actions; tests in `Tests/` read as behaviour.
- **`BaseTest`** (`Support/BaseTest.cs`) owns the Playwright/browser/context/page
  lifecycle, ignores the self-signed dev HTTPS cert, and provides Blazor-aware
  waits (`WaitForBlazorAsync`) for the SignalR circuit to come up.
- **Serial execution** — `.runsettings` disables parallelism. The app uses a
  single shared SQLite DB and the "first-user-is-admin" rule, so concurrent
  tests would race. Run with `--settings tests/Daleel.E2E.Tests/.runsettings`
  (or it is picked up automatically by IDEs).
- **Static-SSR auth** — `/login`, `/register`, `/logout` are plain HTML forms
  (not Blazor circuits); the auth page objects submit them and follow the 302.

## Test → test-case mapping

| Test class | Covers (from `UI_TEST_CASES.md`) |
| --- | --- |
| `LandingPageTests` | LAND-01, LAND-02, LAND-05, LAND-06 |
| `AuthFlowTests` | REG-06, REG-03, LOGIN-03, LOGIN-04, HOME-02/03/06 |
| `ProductDetailTests` | RES-11, PDLG-01, PDLG-04 |
| `CompareTests` | CMP-01, CMP-03, CMP-04 |
| `AdminTests` | ADM-AC-03, ADM-DASH-01, ADM-USR-01/03/04 |
| `AdminUsageTests` | ADM-USE-01/02/03/07 |
| `SearchProgressTests` | HOME-03, HOME-04 |
| `LanguageToggleTests` | LAND-05, LAND-07, X-01 |
| `DirectUrlDetailTests` | PROD-01, BRD-01, STORE-01 |

## Notes on live-data tests

Searches hit live providers, so result-dependent assertions (cards, comparison
tables) are written defensively: they wait generously and `Assert.Ignore` if a
query returns nothing, rather than producing false failures. The progress UI,
navigation, auth, i18n, and admin-control assertions are deterministic.
