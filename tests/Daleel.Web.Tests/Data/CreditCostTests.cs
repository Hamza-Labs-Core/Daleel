using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

public class CreditCostTests
{
    [Theory]
    [InlineData("serpapi", "search", 0, 0, 0.01, 5)]      // flat SerpAPI page
    [InlineData("google-places", "details", 0, 0, 0.02, 3)] // flat Places
    [InlineData("context.dev", "scrape/markdown", 0, 0, 0.001, 2)] // scrape
    [InlineData("context.dev", "brand/ai/products", 0, 0, 0.05, 10)] // catalogue crawl
    [InlineData("context.dev", "catalog/extract", 0, 0, 0.002, 10)] // gateway's canonical catalogue endpoint
    [InlineData("scrape-worker/context.dev", "catalog/extract", 0, 0, 0.0022, 10)] // edge-submitted crawl
    [InlineData("scrape-worker/context.dev", "scrape/markdown", 0, 0, 0.0012, 2)] // edge page fetch = scrape
    [InlineData("workers-ai/classify", "classify/text", 0, 0, 0.002, 1)] // edge inference: flat weight
    [InlineData("workers-ai/filter", "filter/images", 0, 0, 0.002, 1)]
    [InlineData("cloudflare/drain", "catalog", 0, 0, 0.0005, 1)]         // drained edge result
    [InlineData("search-worker/google-places", "places/text-search", 0, 0, 0.0172, 3)] // proxied = direct
    [InlineData("cache", "result/hit", 0, 0, 0, 0)]        // cache hit is free
    [InlineData("openrouter", "chat", 1500, 600, 0.03, 3)] // LLM: ceil(2100/1000)=3
    [InlineData("mystery-co", "x", 0, 0, 0.0042, 5)]       // unknown → ceil($0.0042 × 1000)=5
    public void ForCall_prices_each_provider(
        string provider, string endpoint, int inTok, int outTok, double cost, int expected)
    {
        CreditCost.ForCall(provider, endpoint, inTok == 0 ? null : inTok, outTok == 0 ? null : outTok, (decimal)cost)
            .Should().Be(expected);
    }

    [Fact]
    public void Llm_call_costs_at_least_one_credit()
    {
        CreditCost.ForCall("openrouter", "chat", 10, 5, 0m).Should().Be(1);
    }
}
