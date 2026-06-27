using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<ProductProfile> ProductProfiles => Set<ProductProfile>();
    public DbSet<BrandModel> BrandModels => Set<BrandModel>();
    public DbSet<ScrapedPrice> ScrapedPrices => Set<ScrapedPrice>();
    public DbSet<VisionMatchCache> VisionMatchCaches => Set<VisionMatchCache>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Stores a string list as a JSON text column. The accompanying ValueComparer lets EF detect
        // in-place mutations to the list (without it, change tracking treats the reference as constant).
        var stringListConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrEmpty(v)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
        var stringListComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            // JSON deserialization can yield null list entries, so hash null-safely.
            c => c == null ? 0 : c.Aggregate(0, (h, s) => HashCode.Combine(h, s == null ? 0 : s.GetHashCode())),
            c => c.ToList());

        // Profiles persist LastRefreshed as Unix-ms: SQLite can't translate DateTimeOffset ordering
        // (<, <=) in a WHERE clause, and the staleness sweep filters on exactly that. Same trick as
        // SearchCache.ExpiresAt below.
        var toUnixMs = new ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

        builder.Entity<Brand>(e =>
        {
            // Upsert/lookup is an exact match on the normalized name, so it's unique + indexed.
            // The LastRefreshed index serves the staleness sweep (delete/refresh where older than cutoff).
            e.HasIndex(x => x.NameKey).IsUnique();
            e.HasIndex(x => x.LastRefreshed);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.NameKey).HasMaxLength(200);
            e.Property(x => x.CountryOfOrigin).HasMaxLength(100);
            e.Property(x => x.PriceRange).HasMaxLength(100);
            e.Property(x => x.Website).HasMaxLength(500);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Pros).HasConversion(stringListConverter, stringListComparer);
            e.Property(x => x.Cons).HasConversion(stringListConverter, stringListComparer);
            e.Property(x => x.PopularModels).HasConversion(stringListConverter, stringListComparer);
            e.Property(x => x.LastRefreshed).HasConversion(toUnixMs);
        });

        builder.Entity<Store>(e =>
        {
            e.HasIndex(x => x.NameKey).IsUnique();
            e.HasIndex(x => x.LastRefreshed);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.NameKey).HasMaxLength(200);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Website).HasMaxLength(500);
            e.Property(x => x.BrandsCarried).HasConversion(stringListConverter, stringListComparer);
            e.Property(x => x.Rating);
            // Contact + Google-Maps verification columns (added with store enrichment).
            e.Property(x => x.Phone).HasMaxLength(64);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.GooglePlaceId).HasMaxLength(256);
            e.Property(x => x.GoogleMapsUrl).HasMaxLength(500);
            e.Property(x => x.OpeningHours).HasConversion(stringListConverter, stringListComparer);
            e.Property(x => x.LastRefreshed).HasConversion(toUnixMs);
        });

        builder.Entity<ProductProfile>(e =>
        {
            // Upsert/lookup keyed on the normalized brand+model; LastRefreshed index backs staleness.
            e.HasIndex(x => x.NameKey).IsUnique();
            e.HasIndex(x => x.LastRefreshed);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.NameKey).HasMaxLength(300);
            e.Property(x => x.Brand).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Details).HasMaxLength(8000);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.LastRefreshed).HasConversion(toUnixMs);
        });

        builder.Entity<BrandModel>(e =>
        {
            // Models are unique per brand by normalized name, so re-harvesting upserts in place.
            // The LastRefreshed index backs the staleness sweep; BrandId is indexed for the per-brand
            // model listing the UI shows.
            e.HasIndex(x => new { x.BrandId, x.ModelKey }).IsUnique();
            e.HasIndex(x => x.LastRefreshed);
            e.Property(x => x.ModelName).HasMaxLength(300);
            e.Property(x => x.ModelKey).HasMaxLength(300);
            e.Property(x => x.Category).HasMaxLength(120);
            e.Property(x => x.SpecsJson).HasMaxLength(8000);
            e.Property(x => x.ImageUrl).HasMaxLength(1000);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.Currency).HasMaxLength(16);
            e.Property(x => x.LocalPrice).HasColumnType("decimal(18,2)");
            e.Property(x => x.GlobalPrice).HasColumnType("decimal(18,2)");
            e.Property(x => x.LastRefreshed).HasConversion(toUnixMs);

            // Smart-identification columns: the canonical merged spec sheet (what the UI reads), the
            // R2 pointers, and the discovered image/alias lists (JSON arrays with the shared comparer).
            e.Property(x => x.FinalSpecsJson).HasMaxLength(8000);
            e.Property(x => x.FinalSpecsR2Url).HasMaxLength(1000);
            e.Property(x => x.ImageR2Urls).HasConversion(stringListConverter, stringListComparer);
            e.Property(x => x.RegionalAliases).HasConversion(stringListConverter, stringListComparer);
            e.Property(x => x.DiscoveredAt).HasConversion(toUnixMs);

            // A model belongs to one brand; deleting a brand removes its harvested models.
            e.HasOne(x => x.Brand)
                .WithMany()
                .HasForeignKey(x => x.BrandId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<VisionMatchCache>(e =>
        {
            // One verdict per (store image, brand model) pair — the unique index both enforces that and
            // serves the pre-flight "have we already matched this pair?" lookup the identifier does.
            e.HasIndex(x => new { x.StoreImageHash, x.BrandModelId }).IsUnique();
            e.Property(x => x.StoreImageHash).HasMaxLength(64);
            e.Property(x => x.MatchedModelName).HasMaxLength(300);
            e.Property(x => x.MatchedAt).HasConversion(toUnixMs);

            // The verdict is meaningless once its brand model is gone — cascade it away.
            e.HasOne(x => x.BrandModel)
                .WithMany()
                .HasForeignKey(x => x.BrandModelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ScrapedPrice>(e =>
        {
            // Append-only time series. The hot read is "latest prices for this model", so index
            // (ProductKey, ScrapedAt); the per-store and recency sweeps get their own indexes.
            e.HasIndex(x => new { x.ProductKey, x.ScrapedAt });
            e.HasIndex(x => x.ScrapedAt);
            e.Property(x => x.ProductName).HasMaxLength(300);
            e.Property(x => x.ProductKey).HasMaxLength(300);
            e.Property(x => x.StoreName).HasMaxLength(200);
            e.Property(x => x.Currency).HasMaxLength(16);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.Price).HasColumnType("decimal(18,2)");

            // ScrapedAt as Unix-ms so the "since"/"latest" filters translate on SQLite (it can't
            // compare a DateTimeOffset column). Same trick as the profiles' LastRefreshed.
            e.Property(x => x.ScrapedAt).HasConversion(toUnixMs);
        });

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

            // Persist CreatedAt as Unix-ms. SQLite can't translate DateTimeOffset comparisons in a
            // WHERE clause (>= since), which every usage/cost aggregate does — so a long, which
            // translates on any provider. Same trick as SearchCache.ExpiresAt / ProductProfile.
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
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

            // CreatedAt as Unix-ms so CountSinceAsync(>= since) and the newest-first browse translate
            // on SQLite (it can't compare/order a DateTimeOffset column). Same trick as SearchCache.
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
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
                    Id = SubscriptionPlan.BasicId, Name = "Basic", SearchesPerMonth = 5, MonthlyCredits = 500,
                    PriceMonthly = 0m, IsActive = true, SortOrder = 1,
                    FeaturesJson = "[\"500 credits per month\"," + CommonFeatures + "]"
                },
                new SubscriptionPlan
                {
                    Id = SubscriptionPlan.ProId, Name = "Pro", SearchesPerMonth = 50, MonthlyCredits = 5000,
                    PriceMonthly = 9.99m, IsActive = true, SortOrder = 2,
                    FeaturesJson = "[\"5,000 credits per month\"," + CommonFeatures + "]"
                },
                new SubscriptionPlan
                {
                    Id = SubscriptionPlan.UnlimitedId, Name = "Unlimited", SearchesPerMonth = 250, MonthlyCredits = 50000,
                    PriceMonthly = 100m, IsActive = true, SortOrder = 3,
                    FeaturesJson = "[\"50,000 credits per month\"," + CommonFeatures + "]"
                });
        });
    }
}
