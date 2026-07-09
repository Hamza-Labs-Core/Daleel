using System.Globalization;
using Daleel.Core.Observability;

namespace Daleel.Web.Data;

/// <summary>
/// Builds a <see cref="CostEstimator"/> from admin-editable <see cref="SystemConfig"/> pricing
/// values, falling back to the built-in defaults.
/// </summary>
public static class CostConfig
{
    public static async Task<CostEstimator> BuildEstimatorAsync(ISystemConfigService config, CancellationToken ct = default)
    {
        async Task<decimal> Dec(string key, decimal fallback) =>
            decimal.TryParse(await config.GetAsync(key, ct), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        var defaults = new ProviderPricing();
        var pricing = new ProviderPricing
        {
            PerSearch = await Dec("pricing.search", defaults.PerSearch),
            PerScrape = await Dec("pricing.scrape", defaults.PerScrape),
            PerExtract = await Dec("pricing.extract", defaults.PerExtract),
            PerBrandLookup = await Dec("pricing.brand_lookup", defaults.PerBrandLookup),
            PerPlaces = await Dec("pricing.places", defaults.PerPlaces),
            PerSocial = await Dec("pricing.social", defaults.PerSocial),
            PerRender = await Dec("pricing.render", defaults.PerRender),
            PerWorkersAi = await Dec("pricing.workers_ai", defaults.PerWorkersAi),
            PerEdgeRequest = await Dec("pricing.edge_request", defaults.PerEdgeRequest),
            PerEdgeDrain = await Dec("pricing.edge_drain", defaults.PerEdgeDrain),
            // LLM token rates keep the built-in defaults.
        };

        return new CostEstimator(pricing);
    }
}
