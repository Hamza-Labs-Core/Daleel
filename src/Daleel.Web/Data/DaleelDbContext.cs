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
/// This context runs on <b>PostgreSQL</b> — the <c>daleel</c> database on the same server the pipeline
/// event store and Elsa's workflow store use (see <c>PostgresConnection.ResolveAppDatabase</c>). The
/// migrations under <c>Data/Migrations</c> are scaffolded against Npgsql. A few <c>DateTimeOffset</c>
/// columns are still persisted as Unix-ms <c>bigint</c> via value converters: that encoding is
/// provider-agnostic and keeps the range/order filters in the repositories translating identically — it
/// is not a hard requirement, just a stable, provider-agnostic storage choice we kept across the migration.
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
    public DbSet<ImageModerationLog> ImageModerationLogs => Set<ImageModerationLog>();
    public DbSet<ImageModerationRule> ImageModerationRules => Set<ImageModerationRule>();
    public DbSet<ModerationWhitelistEntry> ModerationWhitelist => Set<ModerationWhitelistEntry>();
    public DbSet<ModerationRuleOverride> ModerationRules => Set<ModerationRuleOverride>();
    public DbSet<SearchCache> SearchCache => Set<SearchCache>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<ProductProfile> ProductProfiles => Set<ProductProfile>();
    public DbSet<BrandModel> BrandModels => Set<BrandModel>();

    /// <summary>A brand's discovered site hierarchy: global / regional / local. See <see cref="BrandSite"/>.</summary>
    public DbSet<BrandSite> BrandSites => Set<BrandSite>();
    public DbSet<ScrapedPrice> ScrapedPrices => Set<ScrapedPrice>();

    /// <summary>The VPS token authority: minted worker bearers + admin-stored vendor keys (encrypted).</summary>
    public DbSet<ServiceCredential> ServiceCredentials => Set<ServiceCredential>();

    /// <summary>The durable enrichment work queue — the table IS the queue. See <see cref="EnrichmentWorkItem"/>.</summary>
    public DbSet<EnrichmentWorkItem> EnrichmentWorkItems => Set<EnrichmentWorkItem>();

    /// <summary>Per-search/product/brand work contexts: findings ledger + LLM synthesis. See <see cref="WorkContext"/>.</summary>
    public DbSet<WorkContext> WorkContexts => Set<WorkContext>();

    /// <summary>Index over the R2-stored entity documents (products/services/places). See <see cref="EntityRecord"/>.</summary>
    public DbSet<EntityRecord> EntityRecords => Set<EntityRecord>();
    public DbSet<VisionMatchCache> VisionMatchCaches => Set<VisionMatchCache>();
    public DbSet<TranslationCacheEntry> TranslationCache => Set<TranslationCacheEntry>();
    public DbSet<RelevanceFlag> RelevanceFlags => Set<RelevanceFlag>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ServiceCredentialConfiguration());

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

        // Profiles persist LastRefreshed as Unix-ms bigint — a provider-agnostic encoding the staleness
        // sweep's range filters (<, <=) translate cleanly against. Same trick as SearchCache.ExpiresAt below.
        var toUnixMs = new ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

        // Nullable variant for optional timestamps (e.g. FilteredContentLog.RatedAt).
        var toNullableUnixMs = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null,
            v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null);

        // Search-result emails are opt-out: default the column to true so existing accounts (and any
        // insert that doesn't set it) are opted in. The C# initializer handles new entities; this SQL
        // default backfills the column when the migration adds it to the existing AspNetUsers rows.
        builder.Entity<ApplicationUser>()
            .Property(u => u.EmailSearchResults)
            .HasDefaultValue(true);

        // Relevancy feedback ("not relevant" flags) — the raw signal the learning loop reads. CreatedAt is
        // Unix-ms bigint like the other timestamps; unique on (UserHash, QueryKey, DedupKey) so a re-flag is
        // idempotent; indexed on (QueryKey, DedupKey) for the per-query negative lookup.
        builder.Entity<RelevanceFlag>(e =>
        {
            e.Property(x => x.Query).HasMaxLength(400);
            e.Property(x => x.QueryKey).HasMaxLength(400);
            e.Property(x => x.Target).HasMaxLength(200);
            e.Property(x => x.Geo).HasMaxLength(64);
            e.Property(x => x.DedupKey).HasMaxLength(300);
            e.Property(x => x.StableId).HasMaxLength(64);
            e.Property(x => x.Brand).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(400);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.UserHash).HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
            e.HasIndex(x => new { x.QueryKey, x.DedupKey });
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.UserHash, x.QueryKey, x.DedupKey }).IsUnique();
        });

        // First-class SKU: a stored global-id attribute on the item profile (the upsert key stays NameKey).
        builder.Entity<ProductProfile>().Property(p => p.Sku).HasMaxLength(100);

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
            e.Property(x => x.SocialLinks).HasConversion(stringListConverter, stringListComparer);
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
            e.Property(x => x.SpecsJson).HasMaxLength(8000);
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
            // Site-hierarchy attribution: which level's catalogue (global/regional/local) a row was
            // harvested from, and for which market. Null = legacy rows, treated as global.
            e.Property(x => x.SiteLevel).HasMaxLength(16);
            e.Property(x => x.SiteCountry).HasMaxLength(8);
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

        builder.Entity<BrandSite>(e =>
        {
            // One row per (brand, level, market) — the discovery upsert's key. Its BrandId prefix
            // also serves the per-brand listing. NULLS NOT DISTINCT so the single global row
            // (CountryCode null) is enforced by the index too, not just by the read-then-write.
            e.HasIndex(x => new { x.BrandId, x.Level, x.CountryCode }).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.Level).HasMaxLength(16);
            e.Property(x => x.CountryCode).HasMaxLength(8);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.LastRefreshed).HasConversion(toUnixMs);

            // A site row is meaningless without its brand — cascade it away.
            e.HasOne(x => x.Brand)
                .WithMany()
                .HasForeignKey(x => x.BrandId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EntityRecord>(e =>
        {
            // Thin index over the R2 entity documents. The PK is the entity's stable id (a string), and
            // every column here exists to FIND or TRAVERSE — never to hold rich content (that lives in R2).
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Intent).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(400);
            e.Property(x => x.NameKey).HasMaxLength(400);
            e.Property(x => x.Geo).HasMaxLength(64);
            e.Property(x => x.SearchId).HasMaxLength(64);
            e.Property(x => x.ProductKey).HasMaxLength(300);
            e.Property(x => x.ParentProductKey).HasMaxLength(300);
            e.Property(x => x.R2Key).HasMaxLength(512);
            e.Property(x => x.R2Url).HasMaxLength(1000);
            e.Property(x => x.LastRefreshed).HasConversion(toUnixMs);

            // Lookup/traversal indexes: by search run, by intent+name, by relation, by recency.
            e.HasIndex(x => x.SearchId);
            e.HasIndex(x => new { x.Intent, x.NameKey });
            e.HasIndex(x => x.BrandId);
            e.HasIndex(x => x.StoreId);
            e.HasIndex(x => x.ProductKey);
            e.HasIndex(x => x.LastRefreshed);

            // Relations exist for the graph, but an entity outlives its brand/store: SetNull on delete so
            // pruning a brand/store unlinks the index row rather than deleting it (the R2 doc still stands).
            e.HasOne(x => x.Brand)
                .WithMany()
                .HasForeignKey(x => x.BrandId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Store)
                .WithMany()
                .HasForeignKey(x => x.StoreId)
                .OnDelete(DeleteBehavior.SetNull);
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

            // ScrapedAt as Unix-ms bigint so the "since"/"latest" range filters translate cleanly.
            // Same trick as the profiles' LastRefreshed.
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

        builder.Entity<EnrichmentWorkItem>(e =>
        {
            // The claim query's exact shape: pending-and-eligible (or lease-expired running) by Id.
            // Timestamps are Unix-ms bigints so the raw FOR-UPDATE-SKIP-LOCKED SQL can compare them
            // as plain integers (see EnrichmentWorkQueue.ClaimAsync).
            e.HasIndex(x => new { x.Status, x.NotBefore });
            e.HasIndex(x => x.SearchJobId);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.ResultType).HasMaxLength(40);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.NotBefore).HasConversion(toUnixMs);
            e.Property(x => x.LeaseUntil).HasConversion(toNullableUnixMs);
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
            e.Property(x => x.CompletedAt).HasConversion(toNullableUnixMs);
        });

        builder.Entity<WorkContext>(e =>
        {
            // The append/upsert target — one row per scope-entity per job. This UNIQUE index IS the
            // idempotency backbone: a re-run (synthesis retry, Plan re-lease) updates the same row,
            // never inserts a second. The plain SearchJobId index serves load-all-for-job (page
            // render, prune). Timestamps are Unix-ms bigints like the queue's, for range-filter prunes.
            e.HasIndex(x => new { x.SearchJobId, x.Scope, x.Key }).IsUnique();
            e.HasIndex(x => x.SearchJobId);
            e.Property(x => x.Scope).HasMaxLength(16);
            e.Property(x => x.Key).HasMaxLength(200); // p_<8hex> or a Brand NameKey (also 200)
            e.Property(x => x.FindingsJson).HasMaxLength(8000);
            e.Property(x => x.Synthesis).HasMaxLength(4000);
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
            e.Property(x => x.SynthesizedAt).HasConversion(toNullableUnixMs);
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
            e.Property(x => x.ResponseSummary).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.EstimatedCost).HasColumnType("decimal(12,6)");

            // Persist CreatedAt as Unix-ms bigint: every usage/cost aggregate filters on (>= since),
            // and a long range filter translates cleanly on any provider. Same trick as
            // SearchCache.ExpiresAt / ProductProfile.
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

            // Store the timestamps as Unix-ms bigint. The cache's whole job is to filter and purge by
            // ExpiresAt, and a long range comparison translates cleanly on any provider.
            var toUnixMs = new ValueConverter<DateTimeOffset, long>(
                v => v.ToUnixTimeMilliseconds(),
                v => DateTimeOffset.FromUnixTimeMilliseconds(v));
            e.Property(x => x.ExpiresAt).HasConversion(toUnixMs);
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
        });

        builder.Entity<TranslationCacheEntry>(e =>
        {
            // Lookups are exact composite-key matches (hash + target language), so that pair is unique and
            // indexed — it serves both the cache read and any per-language freshness sweep on CreatedAt.
            e.HasIndex(x => new { x.SourceHash, x.TargetLang }).IsUnique();
            e.Property(x => x.SourceHash).HasMaxLength(64); // SHA-256 hex
            e.Property(x => x.TargetLang).HasMaxLength(8);

            // CreatedAt as Unix-ms bigint so the freshness filter (CreatedAt >= now - MaxAge) translates as
            // a clean long range comparison. Same trick as SearchCache.ExpiresAt.
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
            e.Property(x => x.Rule).HasMaxLength(256);
            e.Property(x => x.Kind).HasMaxLength(64);
            e.Property(x => x.Content).HasMaxLength(300);
            e.Property(x => x.Field).HasMaxLength(32);
            e.Property(x => x.SourceUrl).HasMaxLength(2048);
            e.Property(x => x.ImageUrl).HasMaxLength(2048);
            e.Property(x => x.DecisionSource).HasMaxLength(16);
            e.Property(x => x.ContentHash).HasMaxLength(64);

            // CreatedAt as Unix-ms bigint so CountSinceAsync(>= since) and the newest-first browse
            // translate cleanly as range/order comparisons. Same trick as SearchCache.
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
            e.Property(x => x.RatedAt).HasConversion(toNullableUnixMs);
            e.Property(x => x.AutoReviewedAt).HasConversion(toNullableUnixMs);
            e.Property(x => x.AutoReviewNote).HasMaxLength(300);
            // The auto-reviewer polls for unreviewed rows newest-batch-first.
            e.HasIndex(x => x.AutoReviewedAt);
        });

        builder.Entity<ImageModerationLog>(e =>
        {
            // Browsed newest-first and filtered by decision in the admin "Images" registry page.
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Decision, x.CreatedAt });
            // ONE registry row per distinct image URL — upsert/lookup by URL.
            e.HasIndex(x => x.ImageUrl).IsUnique();
            // The re-evaluation queue: the processor scans for flagged rows oldest-first.
            e.HasIndex(x => x.ReEvalRequestedAt);
            e.Property(x => x.Query).HasMaxLength(2000);
            e.Property(x => x.Geo).HasMaxLength(64);
            e.Property(x => x.ImageUrl).HasMaxLength(2048);
            e.Property(x => x.ItemName).HasMaxLength(512);
            e.Property(x => x.ItemKind).HasMaxLength(32);
            e.Property(x => x.Decision).HasMaxLength(16);
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.DecisionSource).HasMaxLength(16);
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
            e.Property(x => x.ReEvalRequestedAt).HasConversion(toNullableUnixMs);
        });

        builder.Entity<ImageModerationRule>(e =>
        {
            // Listed in prompt/admin order; the composed prompt reads active rows by SortOrder.
            e.HasIndex(x => x.SortOrder);
            e.Property(x => x.Category).HasMaxLength(48);
            e.Property(x => x.Instruction).HasMaxLength(2000);
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
        });

        builder.Entity<ModerationRuleOverride>(e =>
        {
            // The active set is loaded per policy snapshot; the reviewer looks up by term+category.
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.Category, x.Term, x.Language });
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Term).HasMaxLength(128);
            e.Property(x => x.Language).HasMaxLength(8);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.CreatedAt).HasConversion(toUnixMs);
            e.Property(x => x.ResolvedAt).HasConversion(toNullableUnixMs);
        });

        builder.Entity<ModerationWhitelistEntry>(e =>
        {
            // The whole active key set is loaded per search run; Key is looked up on admin undo.
            e.HasIndex(x => x.Key);
            // UNIQUE: at most one whitelist entry per finding. This is the DB-level guarantee that
            // a retried/concurrent "un-filter" click can't create an orphaned duplicate entry that
            // would keep content whitelisted with no UI path to remove it.
            e.HasIndex(x => x.SourceLogId).IsUnique();
            e.Property(x => x.Key).HasMaxLength(2048);
            e.Property(x => x.MatchType).HasMaxLength(16);
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Note).HasMaxLength(300);
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
