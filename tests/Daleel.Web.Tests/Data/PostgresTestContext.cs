using Daleel.Web.Data;
using Microsoft.EntityFrameworkCore;

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
        ConnectionString = PostgresTestServer.CreateFreshDatabase();

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
        // The database itself is left behind; the shared container (and every database on it) is reaped
        // by Testcontainers when the test process exits, so per-test cleanup would only add latency.
    }
}
