using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Events;

/// <summary>
/// The PostgreSQL-backed event store context — deliberately separate from <c>DaleelDbContext</c> and
/// hosted in its own <c>daleel_events</c> database (the app DB uses <c>daleel</c> on the same server).
/// Holding a single append-only table keeps the event firehose off the transactional app database and
/// lets it scale/retain independently.
/// </summary>
public sealed class EventStoreDbContext : DbContext
{
    public EventStoreDbContext(DbContextOptions<EventStoreDbContext> options) : base(options)
    {
    }

    public DbSet<PipelineEvent> Events => Set<PipelineEvent>();

    /// <summary>The unified admin activity timeline (search/workflow/brand/store/item/cache/llm/user/…).</summary>
    public DbSet<SystemEvent> SystemEvents => Set<SystemEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<SystemEvent>(e =>
        {
            e.ToTable("system_events");

            // The timeline's hot query shapes: newest-first time scans, per-category/severity narrowing,
            // and the per-run / per-user drill-downs.
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.Category, x.Timestamp });
            e.HasIndex(x => new { x.Severity, x.Timestamp });
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.UserHash);

            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.Severity).HasMaxLength(16);
            e.Property(x => x.Source).HasMaxLength(96);
            e.Property(x => x.Summary).HasMaxLength(512);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.UserHash).HasMaxLength(64);

            // Free-form detail as native jsonb so it stays queryable in Postgres later.
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
        });

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
