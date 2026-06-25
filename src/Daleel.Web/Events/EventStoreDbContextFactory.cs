using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Daleel.Web.Events;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add … --context EventStoreDbContext</c> can build
/// the model against the Npgsql provider without a running database. Prefers a real connection from
/// the environment but falls back to a localhost placeholder — migration <em>generation</em> only
/// needs the provider, not a live server.
/// </summary>
public sealed class EventStoreDbContextFactory : IDesignTimeDbContextFactory<EventStoreDbContext>
{
    public EventStoreDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
                   ?? "Host=localhost;Port=5432;Database=daleel_events;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql(conn, o => o.MigrationsAssembly(typeof(EventStoreDbContext).Assembly.FullName))
            .Options;

        return new EventStoreDbContext(options);
    }
}
