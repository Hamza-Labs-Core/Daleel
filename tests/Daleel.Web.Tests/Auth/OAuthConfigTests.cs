using Daleel.Web.Auth;
using Daleel.Web.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Daleel.Web.Tests.Auth;

public class OAuthConfigTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("CHANGE_ME", false)]   // the deploy placeholder must never count as configured
    [InlineData("real-client-id", true)]
    public void IsRealValue_TreatsBlankAndPlaceholderAsNotConfigured(string? value, bool expected) =>
        OAuthProviders.IsRealValue(value).Should().Be(expected);

    [Fact]
    public void ProviderDef_DerivesCanonicalKeysAndEnvVarNames()
    {
        var google = OAuthProviders.All.Single(p => p.Name == "Google");
        google.IdConfigKey.Should().Be("Authentication:Google:ClientId");
        google.SecretConfigKey.Should().Be("Authentication:Google:ClientSecret");
        google.IdSysKey.Should().Be("auth.google.client_id");
        google.SecretSysKey.Should().Be("auth.google.client_secret");
        google.IdEnvVar.Should().Be("GOOGLE_OAUTH_CLIENT_ID");
        google.SecretEnvVar.Should().Be("GOOGLE_OAUTH_CLIENT_SECRET");
    }

    [Fact]
    public void Apple_IsListedAsAnUnwiredPlaceholder() =>
        OAuthProviders.All.Single(p => p.Name == "Apple").Supported.Should().BeFalse();

    [Fact]
    public void AddOAuthEnvironmentVariables_MapsFriendlyVarsOntoCanonicalKeys()
    {
        WithEnv("GOOGLE_OAUTH_CLIENT_ID", "gid-123", () =>
        WithEnv("GOOGLE_OAUTH_CLIENT_SECRET", "gsecret-456", () =>
        {
            var config = new ConfigurationManager();
            config.AddOAuthEnvironmentVariables();

            config["Authentication:Google:ClientId"].Should().Be("gid-123");
            config["Authentication:Google:ClientSecret"].Should().Be("gsecret-456");
        }));
    }

    [Fact]
    public void AddOAuthEnvironmentVariables_SkipsPlaceholder_LeavingAppsettingsIntact()
    {
        WithEnv("GITHUB_OAUTH_CLIENT_ID", "CHANGE_ME", () =>
        {
            var config = new ConfigurationManager();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:GitHub:ClientId"] = "from-appsettings",
            });
            config.AddOAuthEnvironmentVariables();

            // A CHANGE_ME env var must not clobber a real appsettings value.
            config["Authentication:GitHub:ClientId"].Should().Be("from-appsettings");
        });
    }

    [Fact]
    public async Task LoadOverrides_ReturnsOnlyAuthRows()
    {
        await WithTempDb(async (connString, db) =>
        {
            db.SystemConfig.Add(new SystemConfig { Key = "auth.google.client_id", Value = "db-id" });
            db.SystemConfig.Add(new SystemConfig { Key = "ratelimit.api_per_minute", Value = "10", Type = "int" });
            await db.SaveChangesAsync();

            var overrides = OAuthConfigStore.LoadOverrides(connString);

            overrides.Should().ContainKey("auth.google.client_id");
            overrides["auth.google.client_id"].Should().Be("db-id");
            overrides.Should().NotContainKey("ratelimit.api_per_minute");
        });
    }

    [Fact]
    public void LoadOverrides_ToleratesMissingTableOrBadConnection() =>
        // Mirrors first-boot before the SystemConfig migration runs: must fall back to config, not throw.
        OAuthConfigStore.LoadOverrides("Data Source=/nonexistent/dir/nope.db").Should().BeEmpty();

    private static void WithEnv(string name, string value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }

    private static async Task WithTempDb(Func<string, DaleelDbContext, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"daleel-oauth-{Guid.NewGuid():N}.db");
        var connString = $"Data Source={path}";
        try
        {
            var options = new DbContextOptionsBuilder<DaleelDbContext>().UseSqlite(connString).Options;
            await using var db = new DaleelDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await body(connString, db);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // release the file handle before delete
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
