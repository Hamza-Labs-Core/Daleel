using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>
/// The application's EF Core context. Inherits the full ASP.NET Core Identity schema
/// (AspNetUsers, AspNetUserLogins for external providers, roles, etc.) via
/// <see cref="IdentityDbContext{TUser}"/> and adds the two app-owned tables.
/// </summary>
/// <remarks>
/// Targets SQLite locally (data/daleel.db) but the model is provider-agnostic, so swapping the
/// connection to Postgres needs only a different <c>UseNpgsql</c> call and a fresh migration.
/// </remarks>
public sealed class DaleelDbContext : IdentityDbContext<ApplicationUser>
{
    public DaleelDbContext(DbContextOptions<DaleelDbContext> options) : base(options)
    {
    }

    public DbSet<SearchHistoryEntry> SearchHistory => Set<SearchHistoryEntry>();
    public DbSet<SavedResult> SavedResults => Set<SavedResult>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<SearchHistoryEntry>(e =>
        {
            // Index the owner column: every query filters on it, and it backs the FK fan-out.
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Query).HasMaxLength(2000);
            e.Property(x => x.QueryType).HasMaxLength(32);
            e.Property(x => x.Geo).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.ResultSummary).HasMaxLength(1000);
        });

        builder.Entity<SavedResult>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.ResultType).HasMaxLength(32);

            // Keep the saved copy if its originating history row is deleted (set FK null).
            e.HasOne(x => x.SearchHistory)
                .WithMany(h => h.SavedResults)
                .HasForeignKey(x => x.SearchHistoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
