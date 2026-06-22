using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;

namespace Daleel.Web.Auth;

/// <summary>
/// Wires up the external login providers. Each provider is registered <i>only</i> when its
/// client id/secret are present in configuration, so the app boots cleanly with empty secrets and
/// the Login page (which enumerates registered schemes) shows exactly the configured providers.
/// </summary>
public static class AuthenticationConfig
{
    public static AuthenticationBuilder AddExternalProviders(
        this AuthenticationBuilder auth, IConfiguration config)
    {
        var section = config.GetSection("Authentication");

        if (Has(section, "Google", "ClientId", "ClientSecret"))
        {
            auth.AddGoogle(o =>
            {
                o.ClientId = section["Google:ClientId"]!;
                o.ClientSecret = section["Google:ClientSecret"]!;
            });
        }

        if (Has(section, "Microsoft", "ClientId", "ClientSecret"))
        {
            auth.AddMicrosoftAccount(o =>
            {
                o.ClientId = section["Microsoft:ClientId"]!;
                o.ClientSecret = section["Microsoft:ClientSecret"]!;
            });
        }

        if (Has(section, "Twitter", "ConsumerKey", "ConsumerSecret"))
        {
            auth.AddTwitter(o =>
            {
                o.ConsumerKey = section["Twitter:ConsumerKey"]!;
                o.ConsumerSecret = section["Twitter:ConsumerSecret"]!;
                o.RetrieveUserDetails = true; // needed to surface email/name
            });
        }

        if (Has(section, "GitHub", "ClientId", "ClientSecret"))
        {
            auth.AddGitHub(o =>
            {
                o.ClientId = section["GitHub:ClientId"]!;
                o.ClientSecret = section["GitHub:ClientSecret"]!;
                o.Scope.Add("user:email"); // GitHub hides email behind this scope
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
