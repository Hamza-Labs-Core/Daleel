using Daleel.Agent;
using Daleel.Core.Models;

namespace Daleel.Web.Email;

/// <summary>A product summarised for the email: just enough to render a card (name, price, thumbnail).</summary>
public sealed record EmailProduct(string Name, string? PriceRange, string? ImageUrl);

/// <summary>A store summarised for the email: a name and (optionally) where it is.</summary>
public sealed record EmailStore(string Name, string? Location);

/// <summary>
/// The flattened, render-ready view of a completed search used by <see cref="SearchResultEmailTemplate"/>.
/// Decoupling the template from the full <see cref="AgentAnswer"/> keeps the HTML builder trivial to test
/// and the mapping (top-3 selection, price-range formatting) in one place.
/// </summary>
public sealed record SearchResultEmailModel
{
    public required string Query { get; init; }

    /// <summary>"en" or "ar" — drives subject/labels (via the localizer) and RTL layout.</summary>
    public required string Language { get; init; }

    public int ProductCount { get; init; }
    public int BrandCount { get; init; }
    public int StoreCount { get; init; }

    public IReadOnlyList<EmailProduct> TopProducts { get; init; } = Array.Empty<EmailProduct>();
    public IReadOnlyList<EmailStore> TopStores { get; init; } = Array.Empty<EmailStore>();

    /// <summary>Absolute link back to the site (the "View Full Report" button target).</summary>
    public required string CtaUrl { get; init; }

    /// <summary>True for Arabic — the template lays the message out right-to-left.</summary>
    public bool IsRtl => string.Equals(Language, "ar", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the email view from a completed search answer.</summary>
    public static SearchResultEmailModel From(AgentAnswer answer, string language, string ctaUrl)
    {
        var products = answer.Products;
        return new SearchResultEmailModel
        {
            Query = string.IsNullOrWhiteSpace(answer.Question) ? string.Empty : answer.Question.Trim(),
            Language = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim(),
            CtaUrl = ctaUrl,
            ProductCount = products?.ProductCount ?? 0,
            BrandCount = products?.BrandCount ?? 0,
            StoreCount = products?.StoreCount ?? 0,
            TopProducts = products is null
                ? Array.Empty<EmailProduct>()
                : products.Models.Take(3)
                    .Select(m => new EmailProduct(m.Name, PriceRangeOf(m), m.ImageUrl))
                    .ToList(),
            TopStores = products is null
                ? Array.Empty<EmailStore>()
                : products.Stores.Take(3)
                    .Select(s => new EmailStore(s.Name, string.IsNullOrWhiteSpace(s.Address) ? null : s.Address))
                    .ToList()
        };
    }

    /// <summary>
    /// Human price range for a model from its priced offers: "320 JD – 1,200 JD", a single price when
    /// the low and high coincide, or null when none of the offers carry a price.
    /// </summary>
    private static string? PriceRangeOf(ProductModel model)
    {
        var priced = model.Offers.Where(o => o.AsMoney is not null).Select(o => o.AsMoney!.Value).ToList();
        if (priced.Count == 0)
        {
            return null;
        }

        var low = priced.MinBy(m => m.Amount);
        var high = priced.MaxBy(m => m.Amount);
        return high.Amount > low.Amount ? $"{low.ToDisplay()} – {high.ToDisplay()}" : low.ToDisplay();
    }
}
