using System.Collections.Concurrent;
using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Core.Llm;
using Daleel.Web.Conversation;
using Daleel.Web.Pipeline;
using Daleel.Web.Profiles;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Proves the persistence wiring the admin workflows page depends on: with Elsa's workflow-management
/// feature enabled, a completed <see cref="SearchWorkflow"/> run can be persisted as a queryable instance,
/// and the run summary we stamp into <c>WorkflowState.Properties</c> survives the round-trip. This is what
/// the old startup assertion forbade — it's safe now that the state carries no live references.
/// </summary>
public class WorkflowInstancePersistenceTests
{
    private const string StrategyJson =
        """{ "queryType": "General", "subject": "tea", "webQueries": [], "shoppingQueries": [], "reasoning": "n/a" }""";

    [Fact]
    public async Task CompletedRun_IsPersisted_AndSummaryRoundTrips()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElsa(elsa =>
        {
            elsa.AddActivitiesFrom<SearchWorkflow>();
            elsa.UseWorkflowManagement();
        });
        services.AddScoped<SearchPipelineState>();
        services.AddScoped<SearchPipelineServices>();
        services.AddSingleton<ICacheStore, InMemoryCache>();
        services.AddSingleton(new ProfileOptions());
        using var provider = services.BuildServiceProvider();

        string instanceId;
        using (var scope = provider.CreateScope())
        {
            var state = scope.ServiceProvider.GetRequiredService<SearchPipelineState>();
            var pipeServices = scope.ServiceProvider.GetRequiredService<SearchPipelineServices>();
            pipeServices.Agent = new AgentService(new FixedLlm(StrategyJson), new AgentOptions { DefaultGeo = "jordan" });
            state.Query = "tell me about tea";
            state.Geo = "jordan";
            state.ResultKey = "k1";
            state.StartedAt = DateTimeOffset.UtcNow;

            var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
            var run = await runner.RunAsync(new SearchWorkflow(), cancellationToken: default);
            run.WorkflowState.Status.Should().Be(WorkflowStatus.Finished);

            // Stamp the run summary the admin page reads back, then persist the instance.
            run.WorkflowState.Properties[WorkflowRunSummary.PropertyKey] =
                JsonSerializer.Serialize(WorkflowRunSummary.From(state));
            var manager = scope.ServiceProvider.GetRequiredService<IWorkflowInstanceManager>();
            var instance = await manager.SaveAsync(run.WorkflowState, default);
            instanceId = instance.Id;
        }

        // A fresh scope (as the admin page would) can query the persisted instance back.
        var store = provider.GetRequiredService<IWorkflowInstanceStore>();
        var found = await store.FindAsync(new WorkflowInstanceFilter { Id = instanceId }, default);
        found.Should().NotBeNull("the completed run is persisted to the instance store");
        found!.Status.Should().Be(WorkflowStatus.Finished);

        var json = (found.WorkflowState.Properties[WorkflowRunSummary.PropertyKey] as JsonElement?)?.GetString()
                   ?? found.WorkflowState.Properties[WorkflowRunSummary.PropertyKey]?.ToString();
        json.Should().NotBeNullOrEmpty();
        var summary = JsonSerializer.Deserialize<WorkflowRunSummary>(json!);
        summary!.Query.Should().Be("tell me about tea");
        summary.Geo.Should().Be("jordan");
        summary.ResultType.Should().Be("ask");
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
}
