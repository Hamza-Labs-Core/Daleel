using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Read/learn access to <see cref="SiteSearchProfile"/> — see the entity for why this exists.</summary>
public interface ISiteSearchProfileRepository
{
    Task<SiteSearchProfile?> GetByDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>Records a harvest outcome: a success upserts the winning template and resets the
    /// failure latch; a failure (template produced nothing) bumps it so a stale template relearns.</summary>
    Task RecordOutcomeAsync(string domain, string? winningTemplate, DateTimeOffset now, CancellationToken ct = default);
}

public sealed class SiteSearchProfileRepository : ISiteSearchProfileRepository
{
    private readonly DaleelDbContext _db;

    public SiteSearchProfileRepository(DaleelDbContext db) => _db = db;

    public Task<SiteSearchProfile?> GetByDomainAsync(string domain, CancellationToken ct = default)
    {
        var key = Normalize(domain);
        return _db.SiteSearchProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Domain == key, ct);
    }

    public async Task RecordOutcomeAsync(
        string domain, string? winningTemplate, DateTimeOffset now, CancellationToken ct = default)
    {
        var key = Normalize(domain);
        if (key.Length == 0)
        {
            return;
        }

        var existing = await _db.SiteSearchProfiles.FirstOrDefaultAsync(p => p.Domain == key, ct);
        if (winningTemplate is not null)
        {
            if (existing is null)
            {
                _db.SiteSearchProfiles.Add(new SiteSearchProfile
                {
                    Domain = key, SearchUrlTemplate = winningTemplate,
                    DiscoveredVia = "probe", LastSuccessAt = now
                });
            }
            else
            {
                existing.SearchUrlTemplate = winningTemplate;
                existing.LastSuccessAt = now;
                existing.ConsecutiveFailures = 0;
            }
        }
        else if (existing is not null)
        {
            existing.ConsecutiveFailures++;
            // A template that keeps yielding nothing is stale (the site was likely redesigned) — discard
            // it so the next harvest falls back to the platform conventions and relearns. Clearing the
            // template makes SiteSearchCandidates treat this domain like an unlearned one.
            if (Pipeline.SiteSearch.SiteSearchLearning.ShouldDiscardTemplate(existing.ConsecutiveFailures))
            {
                existing.SearchUrlTemplate = string.Empty;
                existing.ConsecutiveFailures = 0;
            }
        }
        else
        {
            return; // nothing learned and nothing to decay
        }

        await _db.SaveChangesAsync(ct);
    }

    private static string Normalize(string domain)
    {
        var d = (domain ?? string.Empty).Trim().ToLowerInvariant();
        return d.StartsWith("www.", StringComparison.Ordinal) ? d[4..] : d;
    }
}
