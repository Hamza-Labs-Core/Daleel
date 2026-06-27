using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Daleel.Web.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add … --context DaleelDbContext</c> can build the
/// model against the Npgsql provider without a running database. Prefers a real connection from the
/// environment but falls back to a localhost placeholder — migration <em>generation</em> only needs the
/// provider, not a live server. Mirrors <see cref="Events.EventStoreDbContextFactory"/>.
/// </summary>
public sealed class DaleelDbContextFactory : IDesignTimeDbContextFactory<DaleelDbContext>
{
    public DaleelDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
                   ?? $"Host=localhost;Port=5432;Database={Events.PostgresConnection.DefaultAppDatabase};Username=daleel;Password=daleel";

        var options = new DbContextOptionsBuilder<DaleelDbContext>()
            .UseNpgsql(conn)
            .Options;

        return new DaleelDbContext(options);
    }
}
