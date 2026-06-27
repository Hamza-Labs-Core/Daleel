using Microsoft.Playwright;

namespace Daleel.E2E.Tests.Support;

/// <summary>
/// Navigation helper for full-page (non-SPA) flows. Daleel's auth and culture switches are real
/// HTML form POSTs that 302-redirect to a destination the test can't always predict (login lands on
/// a return URL or back on <c>/login?error=</c>) or that is identical to the source (the culture
/// reload returns to the same path). Neither case is expressible with <c>WaitForURLAsync</c>, so we
/// deliberately use <c>WaitForNavigation</c> — the one primitive that waits for "the next full-page
/// navigation, wherever it goes". The obsolete-API warning is suppressed here, once, with rationale.
/// </summary>
internal static class Nav
{
#pragma warning disable CS0612 // RunAndWaitForNavigationAsync is obsolete; intentional — see class summary.
    public static Task ClickAndAwaitNavigationAsync(IPage page, Func<Task> action) =>
        page.RunAndWaitForNavigationAsync(action);
#pragma warning restore CS0612
}
