using Daleel.Agent;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Search.Abstractions;
using Daleel.Web.Data;
using Daleel.Web.Pipeline.SubWorkflows;
using Daleel.Web.Profiles;
using Daleel.Web.Services;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// One brand's FULL research as a durable work unit: web + social discovery, the site HIERARCHY
/// (global site → regional variant → market-local storefront, recorded as <see cref="BrandSite"/>
/// rows), Context.dev brand intelligence, and a catalogue harvest per discovered site — like a base
/// run's brand pass, but queued. Each step is independently guarded ("is my field already filled /
/// fresh? then skip") and the Brand row is SAVED the moment a step learns something, so a
/// crash/retry resumes instead of repeating, and a brand any search researched within the last
/// 7 days is reused wholesale (the freshness gate). Failures are per-step: one source erroring
/// never blocks the others, and the unit only retries when every attempted source threw.
/// </summary>
public class BrandResearchHandler : IEnrichmentUnitHandler
{
    /// <summary>How long a researched row (site + description known) satisfies the freshness gate.</summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromDays(7);

    private const int MaxSocialLinks = 4;
    private const int MaxDescriptionChars = 4000; // Brand.Description column budget

    /// <summary>Platforms whose profile pages count as a brand's social presence.</summary>
    private static readonly string[] SocialHosts =
        { "facebook.com", "instagram.com", "tiktok.com", "x.com", "twitter.com", "youtube.com" };

    /// <summary>
    /// Arabic country names for the second (Arabic) official-site query. GeoProfile carries no
    /// Arabic display name, so the supported Arabic-first markets are named here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ArabicCountryNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["jordan"] = "الأردن",
            ["saudi"] = "السعودية",
            ["uae"] = "الإمارات",
            ["egypt"] = "مصر"
        };

    private readonly ILogger<BrandResearchHandler> _logger;

    public BrandResearchHandler(ILogger<BrandResearchHandler> logger) => _logger = logger;

    public string Kind => EnrichmentUnit.BrandResearch;
    public async Task<UnitOutcome> ExecuteAsync(
        EnrichmentWorkItem item, EnrichmentUnitContext ctx, CancellationToken ct)
    {
        if (EnrichmentWorkQueue.ReadPayload<BrandPayload>(item.Payload) is not { } payload ||
            string.IsNullOrWhiteSpace(payload.Brand))
        {
            return new UnitOutcome.Kill("unreadable brand payload");
        }

        if (await ctx.Results.LoadAsync(item.SearchJobId, ct) is not { Products: { Models.Count: > 0 } products })
        {
            return UnitOutcome.Ok;
        }

        var repo = ctx.Services.GetRequiredService<IBrandRepository>();
        var row = await repo.GetByNameAsync(payload.Brand, ct) ?? new Brand
        {
            Name = payload.Brand,
            NameKey = Brand.Normalize(payload.Brand)
        };

        // Freshness gate — the cross-search dedupe. A row another search completed recently already
        // holds the site + description (and whatever socials/catalogue it found); re-researching it
        // would be duplicate paid effort, so jump straight to refilling THIS result from it.
        if (DateTimeOffset.UtcNow - row.LastRefreshed <= Freshness &&
            !string.IsNullOrWhiteSpace(row.Website) && !string.IsNullOrWhiteSpace(row.Description))
        {
            _logger.LogInformation("Brand {Brand} fresh — reusing the saved research", payload.Brand);
            await RefillResultAsync(ctx, item, payload, row, products, ct);
            return UnitOutcome.Ok;
        }

        var geo = GeoProfiles.ResolveOrDefault(
            string.IsNullOrWhiteSpace(products.Geo) ? ctx.Job.Geo : products.Geo);
        var brandKey = CompactKey(payload.Brand);
        var attempted = 0;
        var failed = 0;

        // The site hierarchy discovered so far (all levels/markets) — kept current as this run
        // records new levels, so the per-level gates and the harvest see one consistent list.
        var siteRepo = ctx.Services.GetRequiredService<IBrandSiteRepository>();
        var sites = row.Id > 0
            ? (await siteRepo.GetForBrandAsync(row.Id, ct)).ToList()
            : new List<BrandSite>();

        // LLM-ACTOR site discovery (flag-gated, default off): the LLM searches and READS candidate pages
        // to pick the brand's real official/local/regional sites — replacing host-substring matching that
        // misses rebranded/abbreviated domains and can't tell official from reseller. It runs FIRST and
        // records what it finds, so the deterministic blocks below self-skip (Website/HasFreshSite set).
        var actorCfg = ctx.Services.GetService<Daleel.Web.Data.ISystemConfigService>();
        if (actorCfg is not null && await actorCfg.GetBoolAsync(Actor.ActorFlags.BrandResearch, false, ct))
        {
            attempted++;
            try
            {
                var found = await ctx.Services.GetRequiredService<Actor.BrandSiteActor>()
                    .FindAsync(ctx.Agent(), payload.Brand, geo, ct);
                if (found is not null)
                {
                    if (string.IsNullOrWhiteSpace(row.Website) && !string.IsNullOrWhiteSpace(found.Website))
                    {
                        row.Website = found.Website;
                    }
                    if (string.IsNullOrWhiteSpace(row.Description) && !string.IsNullOrWhiteSpace(found.Description))
                    {
                        row.Description = Truncate(found.Description!);
                    }
                    foreach (var link in found.Social)
                    {
                        if (!row.SocialLinks.Contains(link)) row.SocialLinks.Add(link);
                    }

                    row = await SaveAsync(repo, row, ct);
                    if (row.Id > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(row.Website) && !HasFreshSite(sites, BrandSiteLevel.Global, null))
                            await RecordSiteAsync(siteRepo, sites, row.Id, BrandSiteLevel.Global, null, row.Website!, ct);
                        if (!string.IsNullOrWhiteSpace(found.LocalUrl) && !HasFreshSite(sites, BrandSiteLevel.Local, geo.CountryCode))
                            await RecordSiteAsync(siteRepo, sites, row.Id, BrandSiteLevel.Local, geo.CountryCode, found.LocalUrl!, ct);
                        if (!string.IsNullOrWhiteSpace(found.RegionalUrl) && !HasFreshSite(sites, BrandSiteLevel.Regional, geo.CountryCode))
                            await RecordSiteAsync(siteRepo, sites, row.Id, BrandSiteLevel.Regional, geo.CountryCode, found.RegionalUrl!, ct);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Brand {Brand}: actor site discovery failed", payload.Brand);
            }
        }

        // (a) WEB — official site + a snippet description. Skipped when the row already knows them.
        if (string.IsNullOrWhiteSpace(row.Website))
        {
            attempted++;
            try
            {
                var queries = new List<string> { $"{payload.Brand} official site {geo.Country}" };
                if (geo.IsArabicFirst && ArabicCountryNames.TryGetValue(geo.Key, out var arabic))
                {
                    queries.Add($"{payload.Brand} الموقع الرسمي {arabic}");
                }

                var results = await SearchWebAsync(ctx, queries, geo, ct);
                // The official site is the hit whose REGISTRABLE host label carries the brand name
                // ("www.samsung.com/jo" → "Samsung") — never a marketplace listing about the brand.
                var official = results.FirstOrDefault(r =>
                    AgentService.HostLabel(r.Url) is { } label &&
                    brandKey.Length > 0 && CompactKey(label).Contains(brandKey, StringComparison.Ordinal));
                if (official?.Url is { Length: > 0 } url)
                {
                    row.Website = url;
                    if (string.IsNullOrWhiteSpace(row.Description) && !string.IsNullOrWhiteSpace(official.Snippet))
                    {
                        row.Description = Truncate(official.Snippet.Trim());
                    }

                    row = await SaveAsync(repo, row, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Brand {Brand}: official-site search failed", payload.Brand);
            }
        }

        // (a2) GLOBAL SITE — the website discovery result IS the brand's global site: record it as
        // the hierarchy's global level (Brand.Website stays, as the back-compat mirror of global).
        // A pure local write, not a research "source" for the retry accounting — but isolated all
        // the same so a failed save never blocks the steps below.
        if (!string.IsNullOrWhiteSpace(row.Website) && row.Id > 0 &&
            !HasFreshSite(sites, BrandSiteLevel.Global, countryCode: null))
        {
            try
            {
                await RecordSiteAsync(siteRepo, sites, row.Id, BrandSiteLevel.Global, null, row.Website!, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Brand {Brand}: recording the global site failed", payload.Brand);
            }
        }

        // (a3) LOCAL + REGIONAL SITES — geo-scoped discovery of the market-local storefront, plus
        // (best-effort, from the same results) a regional variant. Skipped while a fresh local row
        // exists for this market; a missing regional alone never re-triggers the paid search —
        // regional sites are optional and "absent" is a fine answer.
        if (!HasFreshSite(sites, BrandSiteLevel.Local, geo.CountryCode))
        {
            attempted++;
            try
            {
                var results = await SearchWebAsync(ctx, new[]
                {
                    $"{payload.Brand} {geo.Country} official",
                    $"{payload.Brand} site {geo.CountryCode}"
                }, geo, ct);

                var globalHost = DomainOf(row.Website);

                // LOCAL: the hit must carry the market signal (LocalityClassifier, with the geo-scoped
                // search + its own title/snippet as evidence) AND be the brand's own web estate — the
                // registrable host label names the brand, or it's the global site's /{cc} section.
                var localUrl = results
                    .FirstOrDefault(r => QualifiesAsLocalSite(r, brandKey, geo, globalHost))?.Url;
                if (!string.IsNullOrWhiteSpace(localUrl))
                {
                    if (row.Id == 0)
                    {
                        row = await SaveAsync(repo, row, ct); // the site rows need the brand FK
                    }

                    await RecordSiteAsync(siteRepo, sites, row.Id, BrandSiteLevel.Local, geo.CountryCode, localUrl!, ct);
                }

                // REGIONAL: a brand-owned hit that is neither the global site nor local-qualified but
                // carries a region-ish signal (neighbouring market's ccTLD, a /mea-style path, or the
                // Arabic edition of the global site). Purely opportunistic — absent is fine.
                if (!HasFreshSite(sites, BrandSiteLevel.Regional, countryCode: null))
                {
                    var regional = results
                        .Select(r => (r.Url, Hint: RegionalHintFor(r, brandKey, geo, globalHost, row.Website, localUrl)))
                        .FirstOrDefault(x => x.Hint is not null);
                    if (regional.Hint is not null && !string.IsNullOrWhiteSpace(regional.Url))
                    {
                        if (row.Id == 0)
                        {
                            row = await SaveAsync(repo, row, ct);
                        }

                        await RecordSiteAsync(siteRepo, sites, row.Id, BrandSiteLevel.Regional, regional.Hint, regional.Url!, ct);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Brand {Brand}: local/regional site discovery failed", payload.Brand);
            }
        }

        // (b) INTELLIGENCE — Context.dev brand profile for the (now-known) domain. BrandProfile
        // carries Description + Socials (no popular-models/country fields), so those are the gaps
        // this step can fill.
        if (!string.IsNullOrWhiteSpace(row.Website) &&
            (string.IsNullOrWhiteSpace(row.Description) || row.SocialLinks.Count == 0))
        {
            attempted++;
            try
            {
                var providers = ctx.Services.GetRequiredService<IProviderApi>();
                if (DomainOf(row.Website) is { } domain &&
                    await providers.GetBrandAsync(domain, ct) is { } profile)
                {
                    var learned = false;
                    if (string.IsNullOrWhiteSpace(row.Description) && !string.IsNullOrWhiteSpace(profile.Description))
                    {
                        row.Description = Truncate(profile.Description!.Trim());
                        learned = true;
                    }

                    if (row.SocialLinks.Count == 0 && profile.Socials.Count > 0)
                    {
                        row.SocialLinks = profile.Socials.Values
                            .Where(u => !string.IsNullOrWhiteSpace(u))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(MaxSocialLinks)
                            .ToList();
                        learned = true;
                    }

                    if (learned)
                    {
                        row = await SaveAsync(repo, row, ct);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Brand {Brand}: Context.dev intelligence failed", payload.Brand);
            }
        }

        // (c) SOCIAL — profile discovery through web search (no public social-profile search exists
        // on the agent; Apify fetches posts, not pages). A link counts only when its host is a
        // social platform AND its path/handle names the brand.
        if (row.SocialLinks.Count == 0)
        {
            attempted++;
            try
            {
                var results = await SearchWebAsync(ctx,
                    new[] { $"{payload.Brand} facebook OR instagram {geo.Country}" }, geo, ct);
                var links = results
                    .Select(r => r.Url)
                    .Where(u => u is not null && IsSocialProfileFor(u!, brandKey))
                    .Select(u => u!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxSocialLinks)
                    .ToList();
                if (links.Count > 0)
                {
                    row.SocialLinks = links;
                    row = await SaveAsync(repo, row, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Brand {Brand}: social-profile search failed", payload.Brand);
            }
        }

        // (d) CATALOGUE — unconditional: every discovered site gets its own harvest (HarvestAsync
        // TTL-gates itself per (brand, level) — fresh levels no-op) and persists BrandModel rows
        // stamped with its level. Ordered global→regional→local so on a (BrandId, ModelKey)
        // collision the LOCAL harvest lands last and its price/attribution wins. With no hierarchy
        // discovered yet, the legacy name-based harvest keeps the old behaviour.
        attempted++;
        try
        {
            var catalog = ctx.Services.GetRequiredService<IBrandCatalogService>();
            if (sites.Count == 0)
            {
                await catalog.HarvestAsync(payload.Brand, ct);
            }
            else
            {
                var errors = 0;
                foreach (var site in sites.OrderBy(s => LevelRank(s.Level)))
                {
                    try
                    {
                        await catalog.HarvestAsync(payload.Brand, site.Url, site.Level, site.CountryCode, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        errors++;
                        _logger.LogWarning(ex, "Brand {Brand}: {Level} catalogue harvest failed",
                            payload.Brand, site.Level);
                    }
                }

                if (errors == sites.Count)
                {
                    failed++; // every level failed — the whole step counts as a dead source
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            failed++;
            _logger.LogWarning(ex, "Brand {Brand}: catalogue harvest failed", payload.Brand);
        }

        if (failed == attempted)
        {
            return new UnitOutcome.Retry("all brand research sources failed");
        }

        // Stamp the pass (LastRefreshed) so the freshness gate can skip the next 7 days — the steps
        // above already saved their own findings; this covers the "every field was already filled"
        // pass, which would otherwise never bump the row.
        row = await SaveAsync(repo, row, ct);

        // (e) Refill THIS result from whatever is now known (this run's findings or a prior run's).
        await RefillResultAsync(ctx, item, payload, row, products, ct);

        // Advisory: what this brand pass turned up, for the final synthesis narrative.
        await HandlerHelpers.NoteAsync(ctx, item.SearchJobId, WorkContextScope.Brand,
            Brand.Normalize(payload.Brand), "brandresearch",
            $"sites={sites.Count}; sources ok={attempted - failed}/{attempted}");
        return UnitOutcome.Ok;
    }

    /// <summary>
    /// The searchy seam: web queries through THIS unit's metered agent (lazily built, wired to the
    /// execution's cost collector; results ride the same halal moderation as a base run's gather).
    /// Virtual so tests stub search without faking the concrete <see cref="AgentService"/>.
    /// </summary>
    protected internal virtual async Task<IReadOnlyList<SearchResult>> SearchWebAsync(
        EnrichmentUnitContext ctx, IReadOnlyList<string> queries, GeoProfile geo, CancellationToken ct)
    {
        var bundle = await ctx.Agent().GatherAsync(new SearchStrategy { WebQueries = queries }, geo, ct);
        return bundle.WebResults;
    }

    /// <summary>
    /// Pushes the saved brand back onto the result: brand card URL/reputation/DbId, the models'
    /// regional brand URL (the site hierarchy's most market-relevant level: local for the search's
    /// market, else regional, else global), and the brand-DB read-through (images/specs/prices
    /// freshly harvested by step d — or by any earlier run). Patches only when something changed.
    /// </summary>
    private static async Task RefillResultAsync(
        EnrichmentUnitContext ctx, EnrichmentWorkItem item, BrandPayload payload, Brand row,
        ProductSearchResult products, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<IItemEnrichmentService>();
        List<ProductModel>? filled = null;
        try
        {
            filled = await svc.FillFromBrandDatabaseUnitAsync(products.Models.ToList(), products.Geo, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // The read-through is additive: a failed local read must not void the row updates below.
        }

        // The models' brand-site link prefers the LOCAL storefront for this search's market, then a
        // regional variant, then the global site — best-effort: a failed hierarchy read falls back
        // to the global mirror (row.Website), which is also what pre-hierarchy rows have.
        var siteUrl = row.Website;
        try
        {
            if (row.Id > 0)
            {
                var sites = await ctx.Services.GetRequiredService<IBrandSiteRepository>()
                    .GetForBrandAsync(row.Id, ct);
                var cc = GeoProfiles.ResolveOrDefault(
                    string.IsNullOrWhiteSpace(products.Geo) ? ctx.Job.Geo : products.Geo).CountryCode;
                siteUrl =
                    sites.FirstOrDefault(s => s.Level == BrandSiteLevel.Local &&
                        string.Equals(s.CountryCode, cc, StringComparison.OrdinalIgnoreCase))?.Url
                    ?? sites.FirstOrDefault(s => s.Level == BrandSiteLevel.Regional)?.Url
                    ?? sites.FirstOrDefault(s => s.Level == BrandSiteLevel.Global)?.Url
                    ?? row.Website;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            siteUrl = row.Website; // additive, same rule as the read-through above
        }

        await ctx.Results.PatchAsync(item, answer =>
        {
            if (answer.Products is not { } p)
            {
                return null;
            }

            // Compose the brand-DB fill onto the LIVE models (additive) — a whole-list assign from
            // the pre-research snapshot would revert every concurrent unit's work committed during
            // this unit's minutes-long pass (offers, prices, images, VerifyPage prunes).
            var changed = false;
            List<ProductModel> models;
            if (filled is not null)
            {
                models = HandlerHelpers.MergeAdditive(p.Models, filled, out changed);
            }
            else
            {
                models = p.Models.ToList();
            }

            if (!string.IsNullOrWhiteSpace(siteUrl))
            {
                for (var i = 0; i < models.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(models[i].BrandRegionalUrl) && MatchesBrand(models[i], payload.Brand))
                    {
                        models[i] = models[i] with { BrandRegionalUrl = siteUrl };
                        changed = true;
                    }
                }
            }

            var brands = p.Brands.ToList();
            for (var i = 0; i < brands.Count; i++)
            {
                if (!string.Equals(brands[i].Name, payload.Brand, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var brand = brands[i];
                if (string.IsNullOrWhiteSpace(brand.Url) && !string.IsNullOrWhiteSpace(row.Website))
                {
                    brand = brand with { Url = row.Website };
                }

                // BrandInfo carries no bare Description — the researched description travels as the
                // reputation summary (the same mapping the base run's brand sub-workflow uses).
                if (brand.Reputation is null && !string.IsNullOrWhiteSpace(row.Description))
                {
                    brand = brand with { Reputation = SynthesizeBrandProfileActivity.ToReputation(row) };
                }

                if (brand.DbId is null && row.Id > 0)
                {
                    brand = brand with { DbId = row.Id };
                }

                if (!ReferenceEquals(brand, brands[i]))
                {
                    brands[i] = brand;
                    changed = true;
                }
            }

            return changed ? answer with { Products = p with { Models = models, Brands = brands } } : null;
        }, ct);
    }

    /// <summary>Persists the row NOW (stamping the refresh time) — the step-by-step durability.</summary>
    private static async Task<Brand> SaveAsync(IBrandRepository repo, Brand row, CancellationToken ct)
    {
        row.LastRefreshed = DateTimeOffset.UtcNow;
        return await repo.UpsertAsync(row, ct);
    }

    private static bool MatchesBrand(ProductModel m, string brand) =>
        (m.Brand?.Contains(brand, StringComparison.OrdinalIgnoreCase) ?? false) ||
        m.Name.Contains(brand, StringComparison.OrdinalIgnoreCase);

    // ── Site-hierarchy helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Path segments that mark a brand page as a multi-market REGIONAL section.</summary>
    private static readonly string[] RegionPathSegments = { "mea", "me", "gcc", "africa", "asia" };

    /// <summary>Harvest order: global first, local LAST so its rows win (BrandId, ModelKey) collisions.</summary>
    private static int LevelRank(string level) => level switch
    {
        BrandSiteLevel.Global => 0,
        BrandSiteLevel.Regional => 1,
        _ => 2,
    };

    /// <summary>
    /// True when a fresh (within <see cref="Freshness"/>) site row exists for the level — the
    /// per-level "already discovered recently" gate. Null <paramref name="countryCode"/> matches
    /// any market (used for global, which has none, and for "any regional row").
    /// </summary>
    private static bool HasFreshSite(IReadOnlyList<BrandSite> sites, string level, string? countryCode) =>
        sites.Any(s => s.Level == level &&
                       (countryCode is null ||
                        string.Equals(s.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)) &&
                       DateTimeOffset.UtcNow - s.LastRefreshed <= Freshness);

    /// <summary>Upserts one hierarchy level NOW (the step-by-step durability) and keeps the run's list current.</summary>
    private static async Task RecordSiteAsync(
        IBrandSiteRepository repo, List<BrandSite> sites, int brandId,
        string level, string? countryCode, string url, CancellationToken ct)
    {
        var saved = await repo.UpsertAsync(new BrandSite
        {
            BrandId = brandId,
            Level = level,
            CountryCode = countryCode,
            Url = url,
            LastRefreshed = DateTimeOffset.UtcNow
        }, ct);

        sites.RemoveAll(s => s.Level == level &&
                             string.Equals(s.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase));
        sites.Add(saved);
    }

    /// <summary>
    /// True when a search hit is the brand's LOCAL site for the market: it carries the market
    /// signal (<see cref="LocalityClassifier"/> with the geo-scoped search + the hit's own
    /// title/snippet as evidence) AND is the brand's own estate — the registrable host label names
    /// the brand ("acme.jo", "acme-jordan.com") or it's the global site's /{cc} section.
    /// </summary>
    private static bool QualifiesAsLocalSite(SearchResult r, string brandKey, GeoProfile geo, string? globalHost)
    {
        if (r.Url is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }

        if (!LocalityClassifier.IsLocal(url, geo.CountryCode, geo.Country,
                fromGeoScopedSearch: true, marketEvidence: $"{r.Title} {r.Snippet}"))
        {
            return false;
        }

        if (brandKey.Length > 0 && AgentService.HostLabel(url) is { } label &&
            CompactKey(label).Contains(brandKey, StringComparison.Ordinal))
        {
            return true;
        }

        return globalHost is not null &&
               string.Equals(DomainOf(url), globalHost, StringComparison.OrdinalIgnoreCase) &&
               HasCountryPath(u.AbsolutePath, geo.CountryCode);
    }

    /// <summary>True when a path carries the market's own segment: /jo/, /en-jo/, /jo-en/.</summary>
    private static bool HasCountryPath(string path, string cc) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg =>
            seg.Equals(cc, StringComparison.OrdinalIgnoreCase) ||
            seg.StartsWith(cc + "-", StringComparison.OrdinalIgnoreCase) ||
            seg.EndsWith("-" + cc, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The REGIONAL hint a search hit carries, or null when it isn't a regional brand site. A hit
    /// qualifies when its host label names the brand, it is neither the global site nor
    /// local-qualified, and it shows a region-ish signal: a neighbouring supported market's ccTLD
    /// (→ that cc), a region path segment like /mea (→ the segment), or the Arabic edition of the
    /// global site (→ "ar"). The hint becomes the row's CountryCode — a label, not an ISO claim.
    /// </summary>
    private static string? RegionalHintFor(
        SearchResult r, string brandKey, GeoProfile geo, string? globalHost, string? globalUrl, string? localUrl)
    {
        if (r.Url is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            brandKey.Length == 0 ||
            AgentService.HostLabel(url) is not { } label ||
            !CompactKey(label).Contains(brandKey, StringComparison.Ordinal))
        {
            return null;
        }

        // The global site itself and the local hit belong to their own levels, never regional.
        if (SameUrl(url, globalUrl) || SameUrl(url, localUrl) ||
            QualifiesAsLocalSite(r, brandKey, geo, globalHost))
        {
            return null;
        }

        var host = DomainOf(url);
        if (host is null)
        {
            return null;
        }

        // (1) A neighbouring supported market's ccTLD (acme.ae seen from a Jordan search).
        foreach (var other in GeoProfiles.All)
        {
            if (!other.CountryCode.Equals(geo.CountryCode, StringComparison.OrdinalIgnoreCase) &&
                host.EndsWith("." + other.CountryCode, StringComparison.OrdinalIgnoreCase))
            {
                return other.CountryCode;
            }
        }

        // (2) A region path segment: /mea /me /gcc /africa /asia.
        var segments = u.AbsolutePath.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (RegionPathSegments.Contains(seg, StringComparer.Ordinal))
            {
                return seg;
            }
        }

        // (3) The Arabic-language edition of the global site (…/ar/… section or the ar. subdomain).
        var isGlobalVariant = globalHost is not null &&
                              (string.Equals(host, globalHost, StringComparison.OrdinalIgnoreCase) ||
                               host.EndsWith("." + globalHost, StringComparison.OrdinalIgnoreCase));
        if (isGlobalVariant &&
            (u.Host.StartsWith("ar.", StringComparison.OrdinalIgnoreCase) ||
             segments.Any(s => s == "ar" || s.StartsWith("ar-", StringComparison.Ordinal) ||
                               s.EndsWith("-ar", StringComparison.Ordinal))))
        {
            return "ar";
        }

        return null;
    }

    /// <summary>Trailing-slash-insensitive URL equality for "is this hit that same site" checks.</summary>
    private static bool SameUrl(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the URL is a social-platform page whose path/handle names the brand.</summary>
    private static bool IsSocialProfileFor(string url, string brandKey)
    {
        if (brandKey.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }

        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        return SocialHosts.Any(h =>
                   host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)) &&
               CompactKey(u.AbsolutePath).Contains(brandKey, StringComparison.Ordinal);
    }

    /// <summary>Lower-cased letters/digits only — spacing/punctuation-proof brand↔host matching.</summary>
    private static string CompactKey(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Truncate(string text) =>
        text.Length <= MaxDescriptionChars ? text : text[..MaxDescriptionChars];

    /// <summary>Bare host of a website URL (scheme-optional, www-stripped), or null.</summary>
    private static string? DomainOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var s = url.Trim();
        if (!s.Contains("://", StringComparison.Ordinal))
        {
            s = "https://" + s;
        }

        if (!Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            return null;
        }

        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
    }
}
