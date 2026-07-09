using Daleel.Core.Observability;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Observability;

public class CostEstimatorTests
{
    [Fact]
    public void EstimateLlm_UsesPerMillionTokenRates()
    {
        var est = new CostEstimator();
        // claude-sonnet-4: $3/M in, $15/M out. 1000 in + 500 out → 0.003 + 0.0075 = 0.0105.
        est.EstimateLlm("anthropic/claude-sonnet-4", 1000, 500).Should().Be(0.0105m);
    }

    [Fact]
    public void EstimateLlm_FallsBackForUnknownModel()
    {
        var est = new CostEstimator();
        est.EstimateLlm("some/unknown-model", 1_000_000, 0).Should().Be(3m); // default 3/M input
    }

    [Theory]
    [InlineData("serpapi", "shopping", 0.005)]
    [InlineData("google-places", "places/text-search", 0.017)]
    [InlineData("context.dev", "scrape/markdown", 0.001)]
    [InlineData("context.dev", "extract", 0.002)]
    [InlineData("context.dev", "brand", 0.01)]
    [InlineData("Apify", "social/fetch", 0.01)]
    [InlineData("cloudflare-browser", "render", 0.01)]
    [InlineData("workers-ai/classify", "classify/text", 0.002)]  // edge inference, never a vendor rate
    [InlineData("workers-ai/extract", "extract/products", 0.002)] // NOT pricing.extract
    [InlineData("workers-ai/filter", "filter/images", 0.002)]
    [InlineData("cloudflare/drain", "catalog", 0.0005)]           // queue ops + R2 read per drained result
    [InlineData("scrape-worker/context.dev", "catalog/extract", 0.0022)]   // vendor extract + edge hop
    [InlineData("scrape-worker/context.dev", "scrape/markdown", 0.0012)]   // vendor scrape + edge hop
    [InlineData("scrape-worker/context.dev", "brand/ai/products", 0.0102)] // vendor brand + edge hop
    [InlineData("search-worker/google-places", "places/text-search", 0.0172)] // Places + edge hop
    public void EstimateCall_ByProviderAndEndpoint(string provider, string endpoint, double expected)
    {
        new CostEstimator().EstimateCall(provider, endpoint).Should().Be((decimal)expected);
    }

    [Fact]
    public void CustomPricing_IsRespected()
    {
        var est = new CostEstimator(new ProviderPricing { PerSearch = 0.02m });
        est.EstimateCall("serpapi", "web").Should().Be(0.02m);
    }

    [Fact]
    public void EdgePricing_IsConfigurable()
    {
        var est = new CostEstimator(new ProviderPricing
        {
            PerWorkersAi = 0.01m,
            PerEdgeRequest = 0.001m,
            PerEdgeDrain = 0.004m
        });

        est.EstimateCall("workers-ai/extract", "extract/products").Should().Be(0.01m);
        est.EstimateCall("cloudflare/drain", "catalog").Should().Be(0.004m);
        // 0.001 default scrape + 0.001 custom edge hop.
        est.EstimateCall("scrape-worker/context.dev", "scrape/markdown").Should().Be(0.002m);
    }
}
