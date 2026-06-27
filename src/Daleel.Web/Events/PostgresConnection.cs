using Npgsql;

namespace Daleel.Web.Events;

/// <summary>
/// Resolves the Postgres connection from configuration, accepting either a ready-made Npgsql keyword
/// string (<c>POSTGRES_CONNECTION_STRING</c>) or a URL-form <c>DATABASE_URL</c>
/// (<c>postgres://user:pass@host:port/db</c>) as handed out by most managed Postgres providers.
/// Returns null when neither is set. PostgreSQL is required for the app to run, so a null result is a
/// fatal misconfiguration the host fails fast on (see Program.cs).
/// </summary>
public static class PostgresConnection
{
    /// <summary>Default name of the main application database (Identity + app tables).</summary>
    public const string DefaultAppDatabase = "daleel";

    /// <summary>
    /// The connection for the main application database (<see cref="Data.DaleelDbContext"/>). It reuses
    /// the same Postgres <i>server + credentials</i> as <see cref="Resolve"/> but points at a separate
    /// database — default <c>daleel</c>, overridable via <c>POSTGRES_APP_DATABASE</c> — so the app's
    /// migration history never collides with the event store's (which keeps <c>daleel_events</c>). EF
    /// Core creates the database on first <c>Migrate()</c> if it does not yet exist. Returns null when
    /// no Postgres connection is configured at all.
    /// </summary>
    public static string? ResolveAppDatabase(IConfiguration config)
    {
        var baseConn = Resolve(config);
        if (baseConn is null)
        {
            return null;
        }

        var appDb = Get(config, "POSTGRES_APP_DATABASE") ?? DefaultAppDatabase;
        return new NpgsqlConnectionStringBuilder(baseConn) { Database = appDb }.ConnectionString;
    }

    public static string? Resolve(IConfiguration config)
    {
        var keyword = Get(config, "POSTGRES_CONNECTION_STRING")
                      ?? config.GetConnectionString("EventStore");
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            return keyword;
        }

        var url = Get(config, "DATABASE_URL");
        return string.IsNullOrWhiteSpace(url) ? null : FromUrl(url!);
    }

    private static string? Get(IConfiguration config, string key)
    {
        var v = config[key] ?? Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>Converts a <c>postgres://user:pass@host:port/db?sslmode=…</c> URL into Npgsql keywords.</summary>
    public static string FromUrl(string url)
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var db = uri.AbsolutePath.Trim('/');

        var parts = new List<string>
        {
            $"Host={uri.Host}",
            $"Port={(uri.Port > 0 ? uri.Port : 5432)}",
            $"Database={db}",
            $"Username={Uri.UnescapeDataString(userInfo[0])}"
        };
        if (userInfo.Length > 1)
        {
            parts.Add($"Password={Uri.UnescapeDataString(userInfo[1])}");
        }

        // Honour an explicit sslmode in the query string; default managed Postgres usually needs it.
        var sslMode = ReadSslMode(uri.Query);
        parts.Add($"SSL Mode={MapSslMode(sslMode)}");
        if (!string.Equals(sslMode, "disable", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("Trust Server Certificate=true");
        }

        return string.Join(";", parts);
    }

    /// <summary>Pulls the <c>sslmode</c> value out of a raw <c>?a=b&amp;sslmode=require</c> query string.</summary>
    private static string? ReadSslMode(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }
        return null;
    }

    private static string MapSslMode(string? sslmode) => sslmode?.ToLowerInvariant() switch
    {
        "disable" => "Disable",
        "allow" => "Allow",
        "prefer" => "Prefer",
        "verify-ca" => "VerifyCA",
        "verify-full" => "VerifyFull",
        _ => "Require"
    };
}
