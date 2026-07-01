using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// A throwaway <see cref="DaleelDbContext"/> backed by its own fresh database on the shared
/// <see cref="PostgresTestServer"/>. Running against real PostgreSQL (not the EF in-memory provider)
/// means the tests exercise the same SQL translation — including the WHERE filters that enforce user
/// isolation — that production runs. Each instance gets a unique database, so tests stay isolated.
/// </summary>
public sealed class PostgresTestContext : IDisposable
{
    /// <summary>Connection string to this test's dedicated database.</summary>
    public string ConnectionString { get; }

    public PostgresTestContext()
    {
        // Cap this database's connection pool. Each test gets a UNIQUE database → a UNIQUE Npgsql pool that
        // no other test reuses, so without a cap the idle connections from every throwaway pool accumulate on
        // the shared server (Postgres default max_connections = 100) until it refuses new clients with
        // "53300: sorry, too many clients already" — a flake that fails whichever test is unlucky at the peak.
        // A small cap is plenty (these tests use one connection at a time) and bounds concurrent usage.
        ConnectionString = new NpgsqlConnectionStringBuilder(PostgresTestServer.CreateFreshDatabase())
        {
            MaxPoolSize = 4,
            MinPoolSize = 0,
        }.ConnectionString;

        Db = NewContext();
        Db.Database.EnsureCreated();
    }

    public DaleelDbContext Db { get; }

    /// <summary>Builds a fresh context over the same database (simulates a new request scope).</summary>
    public DaleelDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DaleelDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new DaleelDbContext(options);
    }

    public void Dispose()
    {
        Db.Dispose();
        // Disposing the context returns its connections to this database's pool but leaves them physically
        // open on the server. Since every test uses a distinct database (distinct pool that is never reused),
        // those idle connections would linger and, across many parallel throwaway databases, exhaust the
        // server's client slots ("53300: too many clients already"). Close this pool now to free the slots
        // immediately. The database itself is left behind — the shared container is reaped by Testcontainers
        // when the test process exits, so dropping per-test databases would only add latency.
        using var probe = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(probe);
    }
}
