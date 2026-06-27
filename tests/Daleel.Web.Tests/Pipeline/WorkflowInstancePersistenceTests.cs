using System.Collections.Concurrent;
using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Core.Llm;
using Daleel.Web.Pipeline;
using Daleel.Web.Profiles;
using Elsa.EntityFrameworkCore.Extensions;
using Elsa.EntityFrameworkCore.Modules.Management;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Proves the durable-persistence wiring the admin workflows page depends on: with Elsa's
/// workflow-management feature on Elsa's EF Core store, a completed <see cref="SearchWorkflow"/> run is
/// persisted as a queryable instance (after the provider migrations create the schema), and the run summary
/// we stamp into <c>WorkflowState.Properties</c> survives the round-trip. The production app persists to
/// Postgres; this test drives the same Elsa Postgres EF store against a throwaway database on the shared
/// test container.
/// </summary>
public class WorkflowInstancePersistenceTests
{
    private const string StrategyJson =
        """{ "queryType": "General", "subject": "tea", "webQueries": [], "shoppingQueries": [], "reasoning": "n/a" }""";

    // A unique Postgres database per test run so the EF schema + rows are isolated and don't leak across runs.
    private readonly string _connectionString = Daleel.Web.Tests.Data.PostgresTestServer.CreateFreshDatabase();

    [Fact]
    public async Task CompletedRun_IsPersistedToEfStore_AndSummaryRoundTrips()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElsa(elsa =>
        {
            elsa.AddActivitiesFrom<SearchWorkflow>();
            elsa.UseWorkflowManagement(management =>
                management.UseEntityFrameworkCore(ef => ef.UsePostgreSql(_connectionString)));
        });
        services.AddScoped<SearchPipelineState>();
        services.AddScoped<SearchPipelineServices>();
        services.AddSingleton<ICacheStore, InMemoryCache>();
        services.AddSingleton(new ProfileOptions());
        using var provider = services.BuildServiceProvider();

        // A bare ServiceProvider doesn't start hosted services, so apply the Elsa-shipped provider
        // migrations by hand — exactly what RunMigrations does automatically at real app startup.
        await using (var db = await provider.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>()
                         .CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }

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

            // Stamp the run summary the admin page reads back, then persist the instance to the EF store.
            run.WorkflowState.Properties[WorkflowRunSummary.PropertyKey] =
                JsonSerializer.Serialize(WorkflowRunSummary.From(state));
            var manager = scope.ServiceProvider.GetRequiredService<IWorkflowInstanceManager>();
            var instance = await manager.SaveAsync(run.WorkflowState, default);
            instanceId = instance.Id;
        }

        // A fresh scope (as the admin page would) reads the persisted instance back out of Postgres.
        var store = provider.GetRequiredService<IWorkflowInstanceStore>();
        var found = await store.FindAsync(new WorkflowInstanceFilter { Id = instanceId }, default);
        found.Should().NotBeNull("the completed run is persisted to the EF instance store");
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
