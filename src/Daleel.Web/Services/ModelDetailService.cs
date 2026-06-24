using Daleel.Core.Models;

namespace Daleel.Web.Services;

/// <summary>
/// On-demand per-model deep scrape for the product detail panel. Builds an agent from
/// server-side keys and runs an exact-model search (all local price sources + spec sheet +
/// LLM pros/cons), so the UI can lazily enrich a model when the user opens its panel.
/// </summary>
public interface IModelDetailService
{
    /// <summary>Deep-scans a specific model in a market. Returns null when scraping is unavailable or nothing matches.</summary>
    Task<ProductModel?> GetAsync(string model, string geo, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ModelDetailService : IModelDetailService
{
    private readonly IAgentFactory _agents;

    public ModelDetailService(IAgentFactory agents) => _agents = agents;

    public async Task<ProductModel?> GetAsync(string model, string geo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model) || !_agents.HasLlm())
        {
            return null;
        }

        var agent = _agents.Build(new AgentRequest { Geo = geo });
        return await agent.ResearchModelAsync(model, geo, cancellationToken).ConfigureAwait(false);
    }
}
