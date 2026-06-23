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
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<UserSubscription> UserSubscriptions => Set<UserSubscription>();
    public DbSet<UserQuota> UserQuotas => Set<UserQuota>();
    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();
    public DbSet<SystemConfig> SystemConfig => Set<SystemConfig>();
    public DbSet<SearchJob> SearchJobs => Set<SearchJob>();
    public DbSet<UserConversation> UserConversations => Set<UserConversation>();

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

        builder.Entity<SubscriptionPlan>(e =>
        {
            e.Property(x => x.PriceMonthly).HasColumnType("decimal(10,2)");
            e.Property(x => x.PriceYearly).HasColumnType("decimal(10,2)");

            // The three configurable tiers (admin can edit these later via /admin/plans).
            e.HasData(
                new SubscriptionPlan
                {
                    Id = SubscriptionPlan.BasicId, Name = "Basic", SearchesPerMonth = 5,
                    PriceMonthly = 0m, IsActive = true, SortOrder = 1,
                    FeaturesJson = "[\"5 searches per month\",\"Halal-filtered results\",\"Up to 10 saved results\"]"
                },
                new SubscriptionPlan
                {
                    Id = SubscriptionPlan.ProId, Name = "Pro", SearchesPerMonth = 100,
                    PriceMonthly = 9.99m, IsActive = true, SortOrder = 2,
                    FeaturesJson = "[\"100 searches per month\",\"Full results & JSON export\",\"Unlimited saved results\",\"Priority models\"]"
                },
                new SubscriptionPlan
                {
                    Id = SubscriptionPlan.UnlimitedId, Name = "Unlimited", SearchesPerMonth = null,
                    PriceMonthly = 100m, IsActive = true, SortOrder = 3,
                    FeaturesJson = "[\"Unlimited searches\",\"Everything in Pro\",\"Priority support\"]"
                });
        });
    }
}
