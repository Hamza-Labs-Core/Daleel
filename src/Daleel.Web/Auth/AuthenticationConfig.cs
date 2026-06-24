using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;

namespace Daleel.Web.Auth;

/// <summary>
/// Wires up the external login providers. Each provider is registered <i>only</i> when its
/// client id/secret resolve to non-empty values, so the app boots cleanly with empty secrets and
/// the Login page (which enumerates registered schemes) shows exactly the configured providers.
///
/// Credentials resolve in this order: admin-set <see cref="Data.SystemConfig"/> overrides (passed in
/// from the DB) first, then configuration (appsettings + the friendly env vars mapped in Program.cs).
/// Because handlers are registered once at startup, changing a credential in the admin UI takes
/// effect on the next app restart — which is required anyway to register a newly-enabled scheme.
/// </summary>
public static class AuthenticationConfig
{
    public static AuthenticationBuilder AddExternalProviders(
        this AuthenticationBuilder auth,
        IConfiguration config,
        IReadOnlyDictionary<string, string>? sysOverrides = null)
    {
        var defs = OAuthProviders.All.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        // SystemConfig override wins when present and real; otherwise fall back to configuration.
        string? Pick(string sysKey, string configKey)
        {
            if (sysOverrides is not null &&
                sysOverrides.TryGetValue(sysKey, out var v) &&
                OAuthProviders.IsRealValue(v))
            {
                return v.Trim();
            }

            var fromConfig = config[configKey];
            return OAuthProviders.IsRealValue(fromConfig) ? fromConfig!.Trim() : null;
        }

        // Resolves both halves of a provider's credential, or null if either is missing.
        (string Id, string Secret)? Resolve(string provider)
        {
            var def = defs[provider];
            var id = Pick(def.IdSysKey, def.IdConfigKey);
            var secret = Pick(def.SecretSysKey, def.SecretConfigKey);
            return id is not null && secret is not null ? (id, secret) : null;
        }

        if (Resolve("Google") is { } g)
        {
            auth.AddGoogle(o =>
            {
                o.ClientId = g.Id;
                o.ClientSecret = g.Secret;
            });
        }

        if (Resolve("Microsoft") is { } ms)
        {
            auth.AddMicrosoftAccount(o =>
            {
                o.ClientId = ms.Id;
                o.ClientSecret = ms.Secret;
            });
        }

        if (Resolve("GitHub") is { } gh)
        {
            auth.AddGitHub(o =>
            {
                o.ClientId = gh.Id;
                o.ClientSecret = gh.Secret;
                o.Scope.Add("user:email"); // GitHub hides email behind this scope
            });
        }

        // Apple is a placeholder: its credentials can be stored/shown in the admin UI, but no handler
        // is wired up yet (it needs the SignInWithApple package + a JWT-signed client secret).

        // Twitter and Facebook keep config-only wiring — they use differently-named credential fields
        // (ConsumerKey/Secret, AppId/Secret) and aren't part of the SystemConfig-managed set.
        var section = config.GetSection("Authentication");

        if (Has(section, "Twitter", "ConsumerKey", "ConsumerSecret"))
        {
            auth.AddTwitter(o =>
            {
                o.ConsumerKey = section["Twitter:ConsumerKey"]!;
                o.ConsumerSecret = section["Twitter:ConsumerSecret"]!;
                o.RetrieveUserDetails = true; // needed to surface email/name
            });
        }

        if (Has(section, "Facebook", "AppId", "AppSecret"))
        {
            auth.AddFacebook(o =>
            {
                o.AppId = section["Facebook:AppId"]!;
                o.AppSecret = section["Facebook:AppSecret"]!;
            });
        }

        return auth;
    }

    /// <summary>True when every named key under <paramref name="provider"/> is non-empty.</summary>
    private static bool Has(IConfiguration section, string provider, params string[] keys) =>
        keys.All(k => !string.IsNullOrWhiteSpace(section[$"{provider}:{k}"]));
}
