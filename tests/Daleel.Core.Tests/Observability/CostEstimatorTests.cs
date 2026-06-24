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
}
