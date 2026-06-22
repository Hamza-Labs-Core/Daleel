using Daleel.Core.Models;

namespace Daleel.Pipeline;

/// <summary>
/// Ranks <see cref="DealListing"/>s by a composite score combining discount depth,
/// recency, and store reliability. Pure and deterministic so it is fully unit-testable.
/// </summary>
/// <remarks>
/// The score is a weighted sum of three normalized signals, each in [0, 1]:
/// <list type="bullet">
///   <item><b>Discount</b> (weight 0.5): deeper discounts score higher; a 100% discount
///   saturates at 1.</item>
///   <item><b>Recency</b> (weight 0.3): newer finds score higher, decaying linearly over
///   a configurable horizon.</item>
///   <item><b>Reliability</b> (weight 0.2): the store-reliability hint, passed through.</item>
/// </list>
/// Expired deals are penalized to the floor so they sink below live ones.
/// </remarks>
public sealed class DealScorer
{
    private readonly TimeSpan _recencyHorizon;
    private readonly DateTimeOffset _now;

    private const double DiscountWeight = 0.5;
    private const double RecencyWeight = 0.3;
    private const double ReliabilityWeight = 0.2;

    /// <param name="now">The reference "now" for recency/expiry (injected for testability).</param>
    /// <param name="recencyHorizon">Age at which a deal's recency signal hits zero.</param>
    public DealScorer(DateTimeOffset now, TimeSpan? recencyHorizon = null)
    {
        _now = now;
        _recencyHorizon = recencyHorizon ?? TimeSpan.FromDays(14);
    }

    /// <summary>Returns the score in [0, 1] for a single listing.</summary>
    public double Score(DealListing deal)
    {
        // Expired → push to the bottom.
        if (deal.Expiry is { } expiry && expiry < _now)
        {
            return 0.0;
        }

        var discount = NormalizeDiscount(deal);
        var recency = NormalizeRecency(deal.FoundAt);
        var reliability = Math.Clamp(deal.StoreReliability, 0.0, 1.0);

        return (discount * DiscountWeight) + (recency * RecencyWeight) + (reliability * ReliabilityWeight);
    }

    /// <summary>Scores every listing and returns them sorted best-first.</summary>
    public IReadOnlyList<DealListing> Rank(IEnumerable<DealListing> deals) =>
        deals
            .Select(d => d with { Score = Score(d) })
            .OrderByDescending(d => d.Score)
            .ToList();

    private static double NormalizeDiscount(DealListing deal)
    {
        // Prefer an explicit percentage; otherwise derive from original vs. current price.
        double? percent = deal.DiscountPercent;

        if (percent is null && deal is { OriginalPrice: { } orig, Price: { } now } &&
            orig.Currency == now.Currency && orig.Amount > 0 && now.Amount <= orig.Amount)
        {
            percent = (double)((orig.Amount - now.Amount) / orig.Amount) * 100.0;
        }

        return percent is null ? 0.0 : Math.Clamp(percent.Value / 100.0, 0.0, 1.0);
    }

    private double NormalizeRecency(DateTimeOffset? foundAt)
    {
        if (foundAt is null)
        {
            return 0.5; // unknown age → neutral
        }

        var age = _now - foundAt.Value;
        if (age <= TimeSpan.Zero)
        {
            return 1.0;
        }

        if (age >= _recencyHorizon)
        {
            return 0.0;
        }

        return 1.0 - (age.TotalSeconds / _recencyHorizon.TotalSeconds);
    }
}
