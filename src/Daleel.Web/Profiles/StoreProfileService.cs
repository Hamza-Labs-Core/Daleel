using Daleel.Web.Data;

namespace Daleel.Web.Profiles;

/// <summary>Store-side counterpart to <see cref="IBrandProfileService"/>; same DB-first/staleness contract.</summary>
public interface IStoreProfileService
{
    Task<Store?> GetOrCreateAsync(string storeName, string? geo = null, CancellationToken ct = default);
    Task<Store?> RefreshAsync(string storeName, string? geo = null, CancellationToken ct = default);
    Task<int> RefreshStaleAsync(int max, CancellationToken ct = default);
}

public sealed class StoreProfileService : IStoreProfileService
{
    private readonly IStoreRepository _repo;
    private readonly IProfileResearcher _researcher;
    private readonly ProfileOptions _options;

    public StoreProfileService(IStoreRepository repo, IProfileResearcher researcher, ProfileOptions options)
    {
        _repo = repo;
        _researcher = researcher;
        _options = options;
    }

    public async Task<Store?> GetOrCreateAsync(string storeName, string? geo = null, CancellationToken ct = default)
    {
        var existing = await _repo.GetByNameAsync(storeName, ct).ConfigureAwait(false);
        if (existing is not null && !existing.IsStale(_options.Now(), _options.Ttl))
        {
            return existing;
        }

        // The stale row's website is still a real, previously-verified URL — seed research with it.
        return await TryResearchAndSaveAsync(storeName, geo, existing?.Website, ct).ConfigureAwait(false) ?? existing;
    }

    // A FORCED refresh passes no hint on purpose: the admin reaches for this button precisely when
    // the saved profile looks wrong, and seeding research with the saved (possibly wrong) website
    // would just re-confirm it — the researcher must re-establish the site from scratch.
    public Task<Store?> RefreshAsync(string storeName, string? geo = null, CancellationToken ct = default) =>
        TryResearchAndSaveAsync(storeName, geo, siteUrlHint: null, ct);

    public async Task<int> RefreshStaleAsync(int max, CancellationToken ct = default)
    {
        var cutoff = _options.Now() - _options.Ttl;
        var stale = await _repo.ListStaleAsync(cutoff, max, ct).ConfigureAwait(false);

        var refreshed = 0;
        foreach (var store in stale)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (await TryResearchAndSaveAsync(store.Name, null, store.Website, ct).ConfigureAwait(false) is not null)
            {
                refreshed++;
            }
        }

        return refreshed;
    }

    private async Task<Store?> TryResearchAndSaveAsync(
        string storeName, string? geo, string? siteUrlHint, CancellationToken ct)
    {
        var researched = await _researcher.ResearchStoreAsync(storeName, geo, ct, siteUrlHint).ConfigureAwait(false);
        if (researched is null)
        {
            return null;
        }

        researched.LastRefreshed = _options.Now();
        return await _repo.UpsertAsync(researched, ct).ConfigureAwait(false);
    }
}
