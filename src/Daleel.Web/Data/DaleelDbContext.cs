using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<UserSubscription> UserSubscriptions => Set<UserSubscription>();
    public DbSet<UserQuota> UserQuotas => Set<UserQuota>();
    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();
    public DbSet<SystemConfig> SystemConfig => Set<SystemConfig>();
    public DbSet<SearchJob> SearchJobs => Set<SearchJob>();
    public DbSet<UserConversation> UserConversations => Set<UserConversation>();
    public DbSet<ApiCallLog> ApiCallLogs => Set<ApiCallLog>();
    public DbSet<FilteredContentLog> FilteredContentLogs => Set<FilteredContentLog>();
    public DbSet<SearchCache> SearchCache => Set<SearchCache>();

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

        builder.Entity<UserSubscription>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId);
        });

        builder.Entity<UserQuota>(e => e.HasIndex(x => x.UserId).IsUnique());

        builder.Entity<AnalyticsEvent>(e =>
        {
            e.HasIndex(x => new { x.EventType, x.Timestamp });
            e.Property(x => x.EventType).HasMaxLength(20);
            e.Property(x => x.Query).HasMaxLength(2000);
        });

        builder.Entity<SearchJob>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Status });
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Query).HasMaxLength(2000);
        });

        builder.Entity<ApiCallLog>(e =>
        {
            // Indexed for the two hot query shapes: per-job (UI live log) and per-user-over-time (usage/cost).
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.Provider, x.CreatedAt });
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.Endpoint).HasMaxLength(64);
            e.Property(x => x.RequestSummary).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.EstimatedCost).HasColumnType("decimal(12,6)");
        });

        builder.Entity<SearchCache>(e =>
        {
            // Reads are exact-key lookups, so the key is unique and indexed. The {Layer, ExpiresAt}
            // index serves the weekly purge sweep (delete where ExpiresAt < now) and per-layer stats.
            e.HasIndex(x => x.CacheKey).IsUnique();
            e.HasIndex(x => new { x.Layer, x.ExpiresAt });
            e.Property(x => x.CacheKey).HasMaxLength(80);
            e.Property(x => x.Layer).HasMaxLength(16);

            // Store the timestamps as Unix-ms integers. SQLite can't translate DateTimeOffset
            // ordering comparisons (>, <=) in a WHERE clause, but the cache's whole job is to filter
            // and purge by ExpiresAt — so persist these as longs, which translate on any provider.
            var toUnixMs = new ValueConverter<DateTimeOffset, long>(
                v => v.ToUnixTimeMilliseconds(),
                v => DateTimeOffset.FromUnixTimeMilliseconds(v));
            e.Property(x => x.ExpiresAt).HasConversion(toUnixMs);
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
        });

        builder.Entity<FilteredContentLog>(e =>
        {
            // Browsed newest-first and filtered by category in the admin "Filtered content" log.
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Category, x.CreatedAt });
            e.Property(x => x.Query).HasMaxLength(2000);
            e.Property(x => x.Geo).HasMaxLength(64);
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Rule).HasMaxLength(128);
            e.Property(x => x.Kind).HasMaxLength(64);
            e.Property(x => x.Content).HasMaxLength(300);
        });

        builder.Entity<SubscriptionPlan>(e =>
        {
            e.Property(x => x.PriceMonthly).HasColumnType("decimal(10,2)");
            e.Property(x => x.PriceYearly).HasColumnType("decimal(10,2)");

            // The three configurable tiers (admin can edit these later via /admin/plans).
            // Every tier unlocks the SAME features — the only thing that differs between plans is the
            // monthly search allowance. So each plan's first bullet is its search count and the rest of
            // the list is identical across all three. Keep this in sync with PricingTiers.razor.
            const string CommonFeatures =
                "\"Smart product & price search\"," +
                "\"Price & store comparison\"," +
                "\"Brand reputation & reviews\"," +
                "\"Deal monitoring & alerts\"," +
                "\"Search history & saved results\"," +
                "\"English & Arabic interface\"";

            e.HasData(
                new SubscriptionPlan
                {
                    Id = SubscriptionPlan.BasicId, Name = "Basic", SearchesPerMonth = 5,
                    PriceMonthly = 0m, IsActive = true, SortOrder = 1,
                    FeaturesJson = "[\"5 searches per month\"," + CommonFeatures + "]"
                },
                new SubscriptionPlan
                {
                    Id = SubscriptionPlan.ProId, Name = "Pro", SearchesPerMonth = 50,
                    PriceMonthly = 9.99m, IsActive = true, SortOrder = 2,
                    FeaturesJson = "[\"50 searches per month\"," + CommonFeatures + "]"
                },
                new SubscriptionPlan
                {
                    Id = SubscriptionPlan.UnlimitedId, Name = "Unlimited", SearchesPerMonth = 250,
                    PriceMonthly = 100m, IsActive = true, SortOrder = 3,
                    FeaturesJson = "[\"250 searches per month\"," + CommonFeatures + "]"
                });
        });
    }
}
