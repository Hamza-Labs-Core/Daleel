using Daleel.Agent;
using Daleel.Core.Models;
using Daleel.Web.Conversation;
using Daleel.Web.Pipeline;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Conversation;

/// <summary>
/// A search run that faults after products are extracted (best-effort enrichment or the serialize/cache
/// tail failing) must not throw the products away. <see cref="WorkflowSearchRunner.SalvageResultJson"/> is
/// the recovery seam: it rebuilds a serialized answer from the run state so the user still sees what was
/// found, while the incident is logged for diagnosis.
/// </summary>
public class SalvageResultTests
{
    private static ProductSearchResult OneProduct() => new()
    {
        Query = "iphone 15",
        Country = "Jordan",
        Models = new List<ProductModel>
        {
            new()
            {
                Name = "iPhone 15",
                Brand = "Apple",
                Offers = new List<PriceOffer> { new() { Price = 700m, Currency = "JOD", Source = "Smartbuy" } }
            }
        }
    };

    [Fact]
    public void Salvages_ExtractedProducts_WhenAggregateNeverRan()
    {
        // Fault before the aggregate step: state.Answer is still null, but the products are on the state.
        var state = new SearchPipelineState { Query = "iphone 15", Geo = "jordan", Products = OneProduct() };

        var json = WorkflowSearchRunner.SalvageResultJson(state);

        json.Should().NotBeNullOrEmpty();
        var answer = ResultSerialization.Deserialize<AgentAnswer>(json!);
        answer!.Products!.ProductCount.Should().Be(1);
        answer.Products!.Models[0].Name.Should().Be("iPhone 15");
    }

    [Fact]
    public void Salvages_FromAssembledAnswer_WhenItExists()
    {
        var answer = new AgentAnswer { Question = "iphone 15", Products = OneProduct() };
        var state = new SearchPipelineState { Query = "iphone 15", Products = OneProduct(), Answer = answer };

        var json = WorkflowSearchRunner.SalvageResultJson(state);

        json.Should().NotBeNullOrEmpty();
        ResultSerialization.Deserialize<AgentAnswer>(json!)!.Products!.ProductCount.Should().Be(1);
    }

    [Fact]
    public void SalvagedAnswer_CarriesSearchStrategy_OnProducts()
    {
        // The salvage fallback fires when the aggregate step never stamped the strategy (state.Answer
        // is null) — a faulted run persists this JSON, so it must carry the search object too, or the
        // grid permanently loses the facets/sort for that search.
        var state = new SearchPipelineState
        {
            Query = "iphone 15",
            Geo = "jordan",
            Products = OneProduct(),
            Strategy = new SearchStrategy { QueryType = QueryType.ProductResearch, Subject = "iphone 15" }
        };

        var json = WorkflowSearchRunner.SalvageResultJson(state);

        json.Should().NotBeNullOrEmpty();
        var answer = ResultSerialization.Deserialize<AgentAnswer>(json!);
        answer!.Products!.Strategy.Should().NotBeNull();
        answer.Products!.Strategy!.Subject.Should().Be("iphone 15");
    }

    [Fact]
    public void ReturnsNull_WhenNothingWasExtracted()
    {
        // A genuinely empty run (fault before any products) has nothing worth surfacing — caller then
        // falls back to reporting a hard failure rather than a misleading empty result.
        var state = new SearchPipelineState { Query = "iphone 15", Products = null };

        WorkflowSearchRunner.SalvageResultJson(state).Should().BeNull();
    }
}
