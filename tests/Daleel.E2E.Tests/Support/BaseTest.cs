using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Support;

/// <summary>
/// Base fixture for every E2E test. Owns the Playwright + browser + context + page lifecycle
/// explicitly (rather than via <c>PageTest</c>) so we control launch options — headless/headed,
/// slow-mo, and crucially <see cref="BrowserNewContextOptions.IgnoreHTTPSErrors"/> for the
/// self-signed dev certificate on https://localhost.
///
/// <para>If <c>E2E_BASE_URL</c> is not set there is no app to talk to, so every test
/// <c>Assert.Ignore</c>s. This keeps the project compiling and the suite green-skipped on machines
/// and CI agents without a running server — the tests only execute when pointed at a live app.</para>
/// </summary>
[TestFixture]
public abstract class BaseTest
{
    private IPlaywright _playwright = default!;
    private IBrowser _browser = default!;

    /// <summary>The isolated browser context for the current test (fresh cookies/storage each test).</summary>
    protected IBrowserContext Context = default!;

    /// <summary>The page under test. Recreated per test so state never leaks between cases.</summary>
    protected IPage Page = default!;

    [OneTimeSetUp]
    public async Task GlobalSetUpAsync()
    {
        if (!TestConfig.HasLiveServer)
        {
            return; // Nothing to launch; per-test SetUp will Assert.Ignore.
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !TestConfig.Headed,
            SlowMo = TestConfig.SlowMo,
        });
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        if (!TestConfig.HasLiveServer)
        {
            Assert.Ignore(
                "E2E_BASE_URL is not set. Start the app (dotnet run --project src/Daleel.Web) " +
                "and set E2E_BASE_URL (e.g. https://localhost:7120) to run these browser tests.");
        }

        Context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = TestConfig.BaseUrl,
            IgnoreHTTPSErrors = true,           // dev cert is self-signed
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        });
        Context.SetDefaultTimeout(TestConfig.TimeoutMs);
        Context.SetDefaultNavigationTimeout(TestConfig.TimeoutMs);
        Page = await Context.NewPageAsync();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (Context is not null)
        {
            await Context.CloseAsync();
        }
    }

    [OneTimeTearDown]
    public async Task GlobalTearDownAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    // ── Shared navigation helpers ────────────────────────────────────────────

    /// <summary>
    /// Navigates to a relative path and waits for the DOM to be ready. Blazor Server then upgrades
    /// the static markup to an interactive circuit over SignalR; <see cref="WaitForBlazorAsync"/>
    /// waits for that, but most tests can just assert on a specific element (Playwright auto-waits).
    /// </summary>
    protected async Task GotoAsync(string path)
    {
        await Page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
    }

    /// <summary>
    /// Best-effort wait for the Blazor circuit to be live: the network to settle and the
    /// reconnect overlay (shown only while disconnected) to be absent. Safe to call on any page.
    /// </summary>
    protected async Task WaitForBlazorAsync()
    {
        try
        {
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 10_000 });
        }
        catch (TimeoutException)
        {
            // SignalR keeps a connection open, so networkidle may never fully settle — that's fine.
        }

        // If the reconnect modal is showing, the circuit dropped; wait for it to clear.
        var reconnect = Page.Locator("#components-reconnect-modal");
        if (await reconnect.CountAsync() > 0 && await reconnect.IsVisibleAsync())
        {
            await reconnect.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = TestConfig.TimeoutMs,
            });
        }
    }

    /// <summary>Returns the computed text direction of the document body ("ltr" or "rtl").</summary>
    protected Task<string> GetDirectionAsync() =>
        Page.EvaluateAsync<string>("getComputedStyle(document.body).direction");

    /// <summary>
    /// Creates a second, fully isolated browser context (separate cookies/session) against the same
    /// server. Used when a test needs to act as two different users at once — e.g. registering a
    /// normal user while staying signed in as admin. Caller is responsible for closing it.
    /// </summary>
    protected Task<IBrowserContext> NewIsolatedContextAsync() =>
        _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = TestConfig.BaseUrl,
            IgnoreHTTPSErrors = true,
            Locale = "en-US",
        });
}
