namespace Daleel.E2E.Tests.Support;

/// <summary>
/// Centralizes environment-driven configuration for the E2E suite so tests never hard-code
/// hosts, timeouts, or credentials. Everything is overridable from the shell / CI so the same
/// binary can target a local <c>dotnet run</c>, a Docker compose stack, or a staging URL.
/// </summary>
public static class TestConfig
{
    /// <summary>
    /// Base URL of the running Daleel.Web app. Defaults to the HTTPS dev profile printed by
    /// <c>dotnet run --project src/Daleel.Web</c> (see <c>launchSettings.json</c>). Override with
    /// the <c>E2E_BASE_URL</c> environment variable, e.g. <c>https://localhost:5001</c>.
    /// </summary>
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("E2E_BASE_URL")?.TrimEnd('/')
        ?? "https://localhost:7120";

    /// <summary>
    /// When unset, the suite has no app to talk to. Tests use this to <c>Assert.Ignore</c> instead
    /// of failing, so the project still builds and runs (green-skipped) in environments without a
    /// live server — exactly the CI "compiles but doesn't run" requirement.
    /// </summary>
    public static bool HasLiveServer =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("E2E_BASE_URL"));

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
