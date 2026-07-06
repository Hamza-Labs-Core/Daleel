using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Data;

/// <summary>
/// Covers the admin-tunable pricing keys → <see cref="Daleel.Core.Observability.CostEstimator"/>
/// wiring, in particular the edge keys (pricing.workers_ai / edge_request / edge_drain): a
/// workers-ai call must price at its own rate, never ride pricing.extract, and a worker-fronted
/// call must add the configured hop on top of the vendor rate.
/// </summary>
public class CostConfigTests
{
    [Fact]
    public async Task BuildEstimator_ResolvesEdgePricingKeys()
    {
        var config = new FakeConfig(new()
        {
            ["pricing.workers_ai"] = "0.004",
            ["pricing.edge_request"] = "0.001",
            ["pricing.edge_drain"] = "0.003",
            ["pricing.extract"] = "0.5" // a workers-ai call must NOT pick this up
        });

        var estimator = await CostConfig.BuildEstimatorAsync(config);

        estimator.EstimateCall("workers-ai/classify", "classify/text").Should().Be(0.004m);
        estimator.EstimateCall("workers-ai/extract", "extract/products").Should().Be(0.004m);
        estimator.EstimateCall("cloudflare/drain", "catalog").Should().Be(0.003m);
        // Default 0.001 scrape + the configured 0.001 edge hop.
        estimator.EstimateCall("scrape-worker/context.dev", "scrape/markdown").Should().Be(0.002m);
    }

    [Fact]
    public async Task BuildEstimator_KeepsBuiltInEdgeDefaults_WhenKeysAbsent()
    {
        var estimator = await CostConfig.BuildEstimatorAsync(new FakeConfig(new()));

        estimator.Pricing.PerWorkersAi.Should().Be(0.002m);
        estimator.Pricing.PerEdgeRequest.Should().Be(0.0002m);
        estimator.Pricing.PerEdgeDrain.Should().Be(0.0005m);
    }

    private sealed class FakeConfig : ISystemConfigService
    {
        private readonly Dictionary<string, string> _values;
        public FakeConfig(Dictionary<string, string> values) => _values = values;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) =>
            Task.FromResult(fallback);
        public Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) =>
            Task.FromResult(fallback);
        public Task SetAsync(string key, string value, string type = "string", CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<SystemConfig>> AllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemConfig>>(Array.Empty<SystemConfig>());
        public Task SeedDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
