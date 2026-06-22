using Daleel.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// A throwaway <see cref="DaleelDbContext"/> backed by an in-memory SQLite database. Using real
/// SQLite (rather than the EF in-memory provider) means the tests exercise the same SQL translation
/// — including the WHERE filters that enforce user isolation — that production runs.
/// </summary>
public sealed class SqliteTestContext : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTestContext()
    {
        // A shared :memory: db lives only as long as its connection is open, so we hold one open.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<DaleelDbContext>()
            .UseSqlite(_connection)
            .Options;

        Db = new DaleelDbContext(options);
        Db.Database.EnsureCreated();
    }

    public DaleelDbContext Db { get; }

    /// <summary>Builds a fresh context over the same connection (simulates a new request scope).</summary>
    public DaleelDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DaleelDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new DaleelDbContext(options);
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
