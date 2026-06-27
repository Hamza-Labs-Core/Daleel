namespace Daleel.E2E.Tests.Support;

/// <summary>
/// Centralizes environment-driven configuration for the E2E suite so tests never hard-code
/// hosts, timeouts, or credentials. Everything is overridable from the shell / CI so the same
/// binary can target a local <c>dotnet run</c>, a Docker compose stack, or a staging URL.
/// </summary>
public static class TestConfig
{
    /// <summary>
    /// Base URL of the Daleel app under test. Defaults to the <strong>QA deployment</strong>
    /// (<c>qa-v1.0</c> tag). Override with the <c>E2E_BASE_URL</c> environment variable to target a
    /// different environment, e.g. a local <c>https://localhost:7120</c> dev instance.
    /// </summary>
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("E2E_BASE_URL")?.TrimEnd('/')
        ?? "https://qa-daleel.hamzalabs.dev";

    /// <summary>
    /// Whether there is a live app to drive. The suite targets the QA deployment by default, so this
    /// is normally <c>true</c>. Set <c>E2E_OFFLINE=1</c> to force every test to <c>Assert.Ignore</c>
    /// — useful for build-only CI that has no network access to QA, keeping the run green.
    /// </summary>
    public static bool HasLiveServer =>
        Environment.GetEnvironmentVariable("E2E_OFFLINE") is not ("1" or "true");

    /// <summary>Run headed (visible browser) by setting <c>E2E_HEADED=1</c>. Default headless.</summary>
    public static bool Headed =>
        Environment.GetEnvironmentVariable("E2E_HEADED") is "1" or "true";

    /// <summary>Slow-motion delay in ms between actions for debugging (<c>E2E_SLOWMO</c>).</summary>
    public static float SlowMo =>
        float.TryParse(Environment.GetEnvironmentVariable("E2E_SLOWMO"), out var ms) ? ms : 0;

    /// <summary>
    /// Per-action / navigation timeout in ms. Blazor Server needs generous time on first hit while
    /// the SignalR circuit warms up. Override with <c>E2E_TIMEOUT_MS</c>.
    /// </summary>
    public static float TimeoutMs =>
        float.TryParse(Environment.GetEnvironmentVariable("E2E_TIMEOUT_MS"), out var ms) ? ms : 30_000;

    /// <summary>
    /// Optional pre-seeded admin credentials. If provided, admin tests sign in with these instead of
    /// relying on "first registered user is admin". Set <c>E2E_ADMIN_EMAIL</c> / <c>E2E_ADMIN_PASSWORD</c>.
    /// </summary>
    public static string? AdminEmail => Environment.GetEnvironmentVariable("E2E_ADMIN_EMAIL");
    public static string? AdminPassword => Environment.GetEnvironmentVariable("E2E_ADMIN_PASSWORD");

    /// <summary>Password used for freshly-registered throwaway test users. Meets Identity defaults.</summary>
    public const string DefaultPassword = "Test!2345Pass";

    /// <summary>A unique, valid email for a throwaway user — keyed off ticks so runs never collide.</summary>
    public static string UniqueEmail(string prefix = "e2e") =>
        $"{prefix}-{DateTime.UtcNow.Ticks:x}@example.com";
}
