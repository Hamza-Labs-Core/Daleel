using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Auth;

/// <summary>
/// Static metadata for one external OAuth login provider. Single source of truth shared by the
/// startup wiring (<see cref="AuthenticationConfig"/>), the friendly env-var mapping
/// (<see cref="OAuthConfigExtensions"/>), and the admin UI (<c>/admin/oauth</c>).
/// </summary>
/// <param name="Name">Scheme/config name, e.g. "Google" — also the key under "Authentication:".</param>
/// <param name="DisplayName">Human label shown in the admin UI.</param>
/// <param name="EnvPrefix">Prefix for the friendly deploy env vars, e.g. "GOOGLE".</param>
/// <param name="Supported">
/// True when a handler is actually wired up. False (e.g. Apple) means credentials can be stored and
/// shown in the admin UI, but no sign-in handler is registered yet — a placeholder for future work.
/// </param>
public sealed record OAuthProviderDef(string Name, string DisplayName, string EnvPrefix, bool Supported)
{
    /// <summary>Canonical config key the app reads the client id from (appsettings / mapped env).</summary>
    public string IdConfigKey => $"Authentication:{Name}:ClientId";

    /// <summary>Canonical config key the app reads the client secret from.</summary>
    public string SecretConfigKey => $"Authentication:{Name}:ClientSecret";

    /// <summary>SystemConfig row key for an admin-set client id (takes precedence over config).</summary>
    public string IdSysKey => $"auth.{Name.ToLowerInvariant()}.client_id";

    /// <summary>SystemConfig row key for an admin-set client secret.</summary>
    public string SecretSysKey => $"auth.{Name.ToLowerInvariant()}.client_secret";

    /// <summary>Friendly env var the deploy pipeline writes into .env, e.g. GOOGLE_OAUTH_CLIENT_ID.</summary>
    public string IdEnvVar => $"{EnvPrefix}_OAUTH_CLIENT_ID";

    /// <summary>Friendly env var for the client secret, e.g. GOOGLE_OAUTH_CLIENT_SECRET.</summary>
    public string SecretEnvVar => $"{EnvPrefix}_OAUTH_CLIENT_SECRET";
}

/// <summary>
/// The set of providers the admin UI manages and that participate in the SystemConfig override
/// chain. Twitter and Facebook keep their config-only wiring in <see cref="AuthenticationConfig"/>
/// because they use differently-named credential fields; they're intentionally not listed here.
/// </summary>
public static class OAuthProviders
{
    /// <summary>The deploy placeholder (see create-secrets.sh) — treated as "not configured".</summary>
    public const string Placeholder = "CHANGE_ME";

    public static readonly IReadOnlyList<OAuthProviderDef> All = new[]
    {
        new OAuthProviderDef("Google", "Google", "GOOGLE", Supported: true),
        new OAuthProviderDef("GitHub", "GitHub", "GITHUB", Supported: true),
        new OAuthProviderDef("Microsoft", "Microsoft", "MICROSOFT", Supported: true),
        new OAuthProviderDef("Apple", "Apple", "APPLE", Supported: false),
    };

    /// <summary>True for a usable credential — non-blank and not the deploy CHANGE_ME placeholder.</summary>
    public static bool IsRealValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value.Trim(), Placeholder, StringComparison.Ordinal);
}

/// <summary>Maps the deploy pipeline's friendly OAuth env vars onto canonical config keys.</summary>
public static class OAuthConfigExtensions
{
    /// <summary>
    /// Folds <c>{PREFIX}_OAUTH_CLIENT_ID</c> / <c>_SECRET</c> environment variables (written into
    /// /opt/daleel/.env by the deploy workflow) onto the <c>Authentication:{Provider}:ClientId</c> /
    /// <c>:ClientSecret</c> keys, so the rest of the app only ever reads the canonical keys. Added as
    /// the last in-memory source so it overrides appsettings; blanks and the CHANGE_ME placeholder are
    /// skipped, leaving any appsettings value intact rather than clobbering it with a dud.
    /// </summary>
    public static IConfigurationManager AddOAuthEnvironmentVariables(this IConfigurationManager config)
    {
        var map = new Dictionary<string, string?>();
        foreach (var p in OAuthProviders.All)
        {
            var id = Environment.GetEnvironmentVariable(p.IdEnvVar);
            var secret = Environment.GetEnvironmentVariable(p.SecretEnvVar);
            if (OAuthProviders.IsRealValue(id)) map[p.IdConfigKey] = id!.Trim();
            if (OAuthProviders.IsRealValue(secret)) map[p.SecretConfigKey] = secret!.Trim();
        }

        if (map.Count > 0)
        {
            config.AddInMemoryCollection(map);
        }

        return config;
    }
}

/// <summary>Reads admin-set OAuth credential overrides out of the SystemConfig table at startup.</summary>
public static class OAuthConfigStore
{
    /// <summary>
    /// Loads every <c>auth.*</c> SystemConfig row as a key→value map. Tolerates a missing table: on a
    /// brand-new box the migration that creates SystemConfig only runs <i>after</i> the host is built
    /// (post <c>app.Build()</c>), which is later than auth wiring — so a first boot simply gets an empty
    /// map and falls back to env/appsettings. Returns empty on any error rather than failing startup.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadOverrides(string connectionString)
    {
        try
        {
            var options = new DbContextOptionsBuilder<DaleelDbContext>()
                .UseSqlite(connectionString)
                .Options;
            using var db = new DaleelDbContext(options);
            return db.SystemConfig.AsNoTracking()
                .Where(c => c.Key.StartsWith("auth."))
                .ToDictionary(c => c.Key, c => c.Value);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}
