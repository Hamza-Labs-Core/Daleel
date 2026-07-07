using Daleel.Web.Moderation;
using Microsoft.EntityFrameworkCore;

namespace Daleel.Web.Data;

/// <summary>Persists and queries the admin-managed halal image-moderation rule list.</summary>
public interface IImageModerationRuleRepository
{
    /// <summary>All rules (enabled and disabled), in prompt/admin order, for the admin editor.</summary>
    Task<IReadOnlyList<ImageModerationRule>> ListAllAsync(CancellationToken ct = default);

    /// <summary>The ENABLED rules, in order, as policy rules the prompt is composed from.</summary>
    Task<IReadOnlyList<VisionPolicy.Rule>> ActiveRulesAsync(CancellationToken ct = default);

    /// <summary>Adds a rule at the end of the list. Returns the created row.</summary>
    Task<ImageModerationRule> AddAsync(string category, string instruction, CancellationToken ct = default);

    /// <summary>Edits a rule's category/instruction. No-op when the id is unknown.</summary>
    Task UpdateAsync(int id, string category, string instruction, CancellationToken ct = default);

    /// <summary>Enables/disables a rule (disabled rules are excluded from the prompt). No-op when unknown.</summary>
    Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default);

    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Inserts the built-in defaults when the table is empty (first-run seed, idempotent).</summary>
    Task SeedDefaultsIfEmptyAsync(CancellationToken ct = default);

    /// <summary>Replaces the whole list with the built-in defaults (admin "reset").</summary>
    Task ResetToDefaultsAsync(CancellationToken ct = default);
}

public sealed class ImageModerationRuleRepository : IImageModerationRuleRepository
{
    private readonly DaleelDbContext _db;

    public ImageModerationRuleRepository(DaleelDbContext db) => _db = db;

    public async Task<IReadOnlyList<ImageModerationRule>> ListAllAsync(CancellationToken ct = default) =>
        await _db.ImageModerationRules.AsNoTracking()
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<VisionPolicy.Rule>> ActiveRulesAsync(CancellationToken ct = default) =>
        await _db.ImageModerationRules.AsNoTracking()
            .Where(x => x.Enabled)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new VisionPolicy.Rule(x.Category, x.Instruction))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<ImageModerationRule> AddAsync(string category, string instruction, CancellationToken ct = default)
    {
        var nextOrder = (await _db.ImageModerationRules.MaxAsync(x => (int?)x.SortOrder, ct).ConfigureAwait(false) ?? 0) + 1;
        var row = new ImageModerationRule
        {
            Category = Normalize(category),
            Instruction = Clean(instruction),
            Enabled = true,
            SortOrder = nextOrder,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.ImageModerationRules.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    public async Task UpdateAsync(int id, string category, string instruction, CancellationToken ct = default)
    {
        var row = await _db.ImageModerationRules.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.Category = Normalize(category);
        row.Instruction = Clean(instruction);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
    {
        var row = await _db.ImageModerationRules.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (row is null || row.Enabled == enabled)
        {
            return;
        }

        row.Enabled = enabled;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var row = await _db.ImageModerationRules.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        _db.ImageModerationRules.Remove(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SeedDefaultsIfEmptyAsync(CancellationToken ct = default)
    {
        if (await _db.ImageModerationRules.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        InsertDefaults();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        var all = await _db.ImageModerationRules.ToListAsync(ct).ConfigureAwait(false);
        _db.ImageModerationRules.RemoveRange(all);
        InsertDefaults();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void InsertDefaults()
    {
        var now = DateTimeOffset.UtcNow;
        var order = 1;
        foreach (var rule in VisionPolicy.DefaultRules)
        {
            _db.ImageModerationRules.Add(new ImageModerationRule
            {
                Category = Normalize(rule.Category),
                Instruction = Clean(rule.Instruction),
                Enabled = true,
                SortOrder = order++,
                CreatedAt = now,
            });
        }
    }

    private static string Normalize(string? category) =>
        (category ?? string.Empty).Trim().ToLowerInvariant();

    private static string Clean(string? instruction) =>
        (instruction ?? string.Empty).Trim();
}
