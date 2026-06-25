using System.Collections.Concurrent;
using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Core.Llm;
using Daleel.Web.Data;
using Daleel.Web.Pipeline;
using Daleel.Web.Profiles;
using Elsa.Extensions;
using Elsa.Workflows;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// End-to-end exercise of the Elsa <see cref="SearchWorkflow"/> against an AgentService built from
/// fakes: a fresh run flows plan → gather → analyze → aggregate → cache into a serialized report, and
/// an identical run with a primed cache short-circuits to the stored report.
/// </summary>
public class SearchWorkflowTests
{
    // A General-query strategy the fake planner returns; also doubles as the analyst's text response.
    private const string StrategyJson =
        """{ "queryType": "General", "subject": "tea", "webQueries": [], "shoppingQueries": [], "reasoning": "n/a" }""";

    [Fact]
    public async Task FreshRun_ProducesSerializedReport_AndCachesIt()
    {
        var cache = new InMemoryCache();
        using var provider = BuildProvider(cache);
        var state = await RunAsync(provider, cache, query: "tell me about tea", resultKey: "k1");

        state.FromCache.Should().BeFalse();
        state.ResultJson.Should().NotBeNullOrEmpty();
        state.ResultJson.Should().Contain("tea", "the answer echoes the question");
        (await cache.GetAsync("k1")).Should().NotBeNull("the workflow caches the completed report");
    }

    [Fact]
    public async Task PrimedCache_ShortCircuits_ToStoredReport()
    {
        var cache = new InMemoryCache();
        await cache.SetAsync("k2",
            JsonSerializer.Serialize(new CachedSearchResult("{\"cached\":true}", "ask", 3, "alcohol")),
            TimeSpan.FromDays(1));

        using var provider = BuildProvider(cache);
        var state = await RunAsync(provider, cache, query: "tea again", resultKey: "k2");

        state.FromCache.Should().BeTrue();
        state.ResultJson.Should().Be("{\"cached\":true}");
        state.FilteredCount.Should().Be(3, "the cached moderation telemetry is replayed");
        state.Bundle.Should().BeNull("gather must not run on a cache hit");
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private static async Task<SearchPipelineState> RunAsync(
        ServiceProvider provider, ICacheStore cache, string query, string resultKey)
    {
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<SearchPipelineState>();
        state.Agent = new AgentService(new FixedLlm(StrategyJson), new AgentOptions { DefaultGeo = "jordan" });
        state.Query = query;
        state.Geo = "jordan";
        state.ResultKey = resultKey;
        state.Cache = cache;

        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        var run = await runner.RunAsync(new SearchWorkflow(), cancellationToken: default);
        run.WorkflowState.Status.Should().Be(WorkflowStatus.Finished);
        return state;
    }

    private static ServiceProvider BuildProvider(ICacheStore cache)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElsa(elsa => elsa.AddActivitiesFrom<SearchWorkflow>());
        services.AddScoped<SearchPipelineState>();
        services.AddSingleton(cache);
        // Enrichment + deep-dive only resolve these for product queries; register no-ops for completeness.
        services.AddSingleton<IBrandProfileService, NoopBrandProfiles>();
        services.AddSingleton<IStoreProfileService, NoopStoreProfiles>();
        services.AddSingleton(new ProfileOptions());
        services.AddSingleton<IProductProfileRepository, NoopProductProfiles>();
        return services.BuildServiceProvider();
    }

    private sealed class FixedLlm : ILlmClient
    {
        private readonly string _response;
        public FixedLlm(string response) => _response = response;
        public string Provider => "fake";
        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default) =>
            Task.FromResult(new LlmResponse { Content = _response });
    }

    private sealed class InMemoryCache : ICacheStore
    {
        private readonly ConcurrentDictionary<string, string> _store = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }
        public Task<int> PurgeExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NoopBrandProfiles : IBrandProfileService
    {
        public Task<Brand?> GetOrCreateAsync(string n, string? g = null, CancellationToken ct = default) => Task.FromResult<Brand?>(null);
        public Task<Brand?> RefreshAsync(string n, string? g = null, CancellationToken ct = default) => Task.FromResult<Brand?>(null);
        public Task<int> RefreshStaleAsync(int max, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NoopStoreProfiles : IStoreProfileService
    {
        public Task<Store?> GetOrCreateAsync(string n, string? g = null, CancellationToken ct = default) => Task.FromResult<Store?>(null);
        public Task<Store?> RefreshAsync(string n, string? g = null, CancellationToken ct = default) => Task.FromResult<Store?>(null);
        public Task<int> RefreshStaleAsync(int max, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NoopProductProfiles : IProductProfileRepository
    {
        public Task<ProductProfile?> GetByKeyAsync(string key, CancellationToken ct = default) => Task.FromResult<ProductProfile?>(null);
        public Task<ProductProfile> UpsertAsync(ProductProfile p, CancellationToken ct = default) => Task.FromResult(p);
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
