using Npgsql;
using Testcontainers.PostgreSql;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// A single PostgreSQL container shared by the whole test assembly. It starts once, lazily, on first
/// use and is reaped by Testcontainers (Ryuk) when the test process exits. Each test gets its OWN
/// throwaway database on this server for isolation — the same fresh-state guarantee an in-memory
/// database would give, but against the real engine production runs (see <see cref="PostgresTestContext"/>).
/// </summary>
/// <remarks>
/// On CI (standard Docker socket) the defaults work as-is. On Docker Desktop for Mac, Testcontainers'
/// Ryuk reaper can stall starting against the user socket; if <c>dotnet test</c> hangs locally, run with
/// <c>TESTCONTAINERS_RYUK_DISABLED=true</c> (containers are then reaped on process exit instead).
/// </remarks>
internal static class PostgresTestServer
{
    private static readonly Lazy<PostgreSqlContainer> Container = new(() =>
    {
        var container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();
        container.StartAsync().GetAwaiter().GetResult();
        return container;
    });

    /// <summary>Connection string to the shared server's default database (used to issue CREATE DATABASE).</summary>
    public static string AdminConnectionString => Container.Value.GetConnectionString();

    /// <summary>
    /// Creates a freshly-named, empty database on the shared server and returns a connection string
    /// pointing at it. Callers build their schema via <c>EnsureCreated()</c> or EF migrations.
    /// </summary>
    public static string CreateFreshDatabase()
    {
        var name = $"test_{Guid.NewGuid():N}";

        using (var conn = new NpgsqlConnection(AdminConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // The name is a server-generated GUID literal, never user input — safe to interpolate.
            cmd.CommandText = $"CREATE DATABASE \"{name}\"";
            cmd.ExecuteNonQuery();
        }

        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name }.ConnectionString;
    }
}
