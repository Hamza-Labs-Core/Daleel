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

    // A buy-intent strategy: drives the extract-products path so the run assembles a structured result.
    private const string ProductStrategyJson =
        """{ "queryType": "ProductResearch", "subject": "coffee maker", "webQueries": [], "shoppingQueries": [], "reasoning": "n/a" }""";

    [Fact]
    public async Task FreshRun_ProducesSerializedReport_WithoutWritingTheVault()
    {
        var cache = new InMemoryCache();
        using var provider = BuildProvider(cache);
        var progress = new List<string>();
        var state = await RunAsync(provider, cache, query: "tell me about tea", resultKey: "k1", progress.Add);

        state.FromCache.Should().BeFalse();
        state.ResultJson.Should().NotBeNullOrEmpty();
        state.ResultJson.Should().Contain("tea", "the answer echoes the question");
        // The whole-query result vault was removed: the report is serialized (the runner persists it to
        // SearchJob.ResultJson) but is NOT written to the cache.
        (await cache.GetAsync("k1")).Should().BeNull("the whole-query vault is no longer written");

        // The pipeline must stream structured progress the SearchProgress UI can decode: the run starts
        // on the Analyzing step and ends on Done.
        var signals = progress
            .Select(m => SearchProgressSignal.TryDecode(m, out var s) ? s : (SearchProgressSignal?)null)
            .Where(s => s is not null)
            .Select(s => s!.Value)
            .ToList();
        signals.Should().NotBeEmpty("activities emit encoded progress signals");
        signals.First().Step.Should().Be(SearchStep.Analyzing);
        signals.Should().Contain(s => s.Step == SearchStep.Done, "the run reports completion");
    }

    [Fact]
    public async Task PrimedVault_IsIgnored_SearchRunsFresh()
    {
        // The whole-query result vault was removed: even a primed entry must NOT short-circuit — the search
        // runs fresh, so it never serves stale products/prices and always applies the relevance/learning pass.
        var cache = new InMemoryCache();
        await cache.SetAsync("k2",
            JsonSerializer.Serialize(new CachedSearchResult("{\"cached\":true}", "ask", 3, "alcohol")),
            TimeSpan.FromDays(1));

        using var provider = BuildProvider(cache);
        var state = await RunAsync(provider, cache, query: "tea again", resultKey: "k2");

        state.FromCache.Should().BeFalse("the vault no longer short-circuits a search");
        state.ResultJson.Should().NotBe("{\"cached\":true}", "the stored vault entry is not served");
        state.ResultJson.Should().Contain("tea", "the fresh answer echoes the question");
    }

    [Fact]
    public async Task ProductRun_StampsSearchStrategy_OnFinalAnswerProducts()
    {
        // The aggregate step must attach the planner's search object to the structured result it
        // assembles — that record serializes into SearchJob.ResultJson and is what the grid binds.
        var cache = new InMemoryCache();
        using var provider = BuildProvider(cache);
        var state = await RunAsync(provider, cache, query: "best coffee maker", resultKey: "k3",
            strategyJson: ProductStrategyJson);

        state.Answer.Should().NotBeNull();
        state.Answer!.Products.Should().NotBeNull("a ProductResearch run projects a structured result");
        state.Answer.Products!.Strategy.Should().NotBeNull("the aggregate step stamps the search object");
        state.Answer.Products.Strategy!.Subject.Should().Be("coffee maker");
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private static async Task<SearchPipelineState> RunAsync(
        ServiceProvider provider, ICacheStore cache, string query, string resultKey,
        Action<string>? progress = null, string strategyJson = StrategyJson)
    {
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<SearchPipelineState>();
        var services = scope.ServiceProvider.GetRequiredService<SearchPipelineServices>();
        services.Agent = new AgentService(new FixedLlm(strategyJson), new AgentOptions { DefaultGeo = "jordan" });
        state.Query = query;
        state.Geo = "jordan";
        state.ResultKey = resultKey;
        services.Cache = cache;
        services.Progress = progress;

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
        services.AddScoped<SearchPipelineServices>();
        services.AddSingleton(cache);
        // CheckCache scores a hit's completeness before replaying it — the activity resolves this from context.
        services.AddSingleton<ICacheQualityValidator, CacheQualityValidator>();
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
