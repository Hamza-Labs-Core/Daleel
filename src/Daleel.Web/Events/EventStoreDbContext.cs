using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Events;

/// <summary>
/// The PostgreSQL-backed event store context — deliberately separate from <c>DaleelDbContext</c>
/// (which stays on SQLite). Holding a single append-only table keeps the event firehose off the
/// transactional app database and lets it scale/retain independently.
/// </summary>
public sealed class EventStoreDbContext : DbContext
{
    public EventStoreDbContext(DbContextOptions<EventStoreDbContext> options) : base(options)
    {
    }

    public DbSet<PipelineEvent> Events => Set<PipelineEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PipelineEvent>(e =>
        {
            e.ToTable("pipeline_events");

            // The dashboard's hot query shapes: time-window scans, and group-by provider/category.
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.Category, x.Timestamp });
            e.HasIndex(x => new { x.Provider, x.Timestamp });
            e.HasIndex(x => x.SearchId);

            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(96);
            e.Property(x => x.SearchId).HasMaxLength(64);
            e.Property(x => x.EstimatedCost).HasColumnType("decimal(12,6)");

            // Free-form detail as native jsonb so it stays queryable in Postgres later.
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });
    }
}
