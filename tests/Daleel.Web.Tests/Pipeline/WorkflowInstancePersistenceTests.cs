using System.Collections.Concurrent;
using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Caching;
using Daleel.Core.Llm;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
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

    [Fact]
    public async Task CancelRequestedFlag_ShortCircuitsTheWorkflow_BeforeTheFirstStepRuns()
    {
        // Proves the structural guarantee: because every activity inherits CancellableActivity, the very
        // first step's base-class check sees the durable CancelRequested flag and throws — the workflow
        // never reaches any activity body, with no per-activity opt-in required.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DaleelDbContext>(o => o.UseNpgsql(_connectionString), ServiceLifetime.Transient);
        services.AddElsa(elsa => elsa.AddActivitiesFrom<SearchWorkflow>());
        services.AddScoped<SearchPipelineState>();
        services.AddScoped<SearchPipelineServices>();
        services.AddSingleton<ICacheStore, InMemoryCache>();
        services.AddSingleton(new ProfileOptions());
        using var provider = services.BuildServiceProvider();

        // Build the app schema and seed a job that's already been cancelled.
        int jobId;
        await using (var db = provider.GetRequiredService<DaleelDbContext>())
        {
            await db.Database.EnsureCreatedAsync();
            var job = new SearchJob
            {
                UserId = "u1", Query = "tea", Status = JobStatus.Running, CancelRequested = true,
                CreatedAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow
            };
            db.SearchJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<SearchPipelineState>();
        var pipeServices = scope.ServiceProvider.GetRequiredService<SearchPipelineServices>();
        // The agent must never be called: if the guard fails to fire, ParseQuery would invoke it.
        pipeServices.Agent = new AgentService(new ThrowingLlm(), new AgentOptions { DefaultGeo = "jordan" });
        state.Query = "tell me about tea";
        state.Geo = "jordan";
        state.ResultKey = "k1";
        state.SearchId = jobId.ToString();
        state.StartedAt = DateTimeOffset.UtcNow;

        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        var run = await runner.RunAsync(new SearchWorkflow(), cancellationToken: default);

        // The run did not finish, and the first activity's body never ran (Strategy is still null — the
        // planner was never invoked, which is also why the ThrowingLlm never threw).
        run.WorkflowState.SubStatus.Should().NotBe(WorkflowSubStatus.Finished);
        state.Strategy.Should().BeNull();
    }

    [Fact]
    public async Task CancelCheckDurableReadFailure_DoesNotFaultTheRun_CooperativeCheckIsBestEffort()
    {
        // Regression guard for the "every search returns no results" outage: the per-activity cancel check
        // is the BEST-EFFORT cooperative layer. If its durable CancelRequested read fails (a transient DB
        // blip, pool exhaustion, or — the real cause here — a not-yet-migrated CancelRequested column on a
        // freshly-deployed instance), that failure must NOT throw out of the first activity and fault the
        // whole run before any products exist to salvage. It must degrade to "not cancelled, keep running".
        //
        // We reproduce a failing durable read by registering a DaleelDbContext whose schema is NEVER
        // created: every `SELECT CancelRequested FROM SearchJobs` throws "relation does not exist". With the
        // guard hardened, ParseQuery's body still runs (Strategy is populated by the planner); without it,
        // the run faults on step 1 and Strategy stays null.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DaleelDbContext>(o => o.UseNpgsql(_connectionString), ServiceLifetime.Transient);
        services.AddElsa(elsa => elsa.AddActivitiesFrom<SearchWorkflow>());
        services.AddScoped<SearchPipelineState>();
        services.AddScoped<SearchPipelineServices>();
        services.AddSingleton<ICacheStore, InMemoryCache>();
        services.AddSingleton(new ProfileOptions());
        using var provider = services.BuildServiceProvider();

        // Deliberately do NOT create the DaleelDbContext schema — the SearchJobs table does not exist, so the
        // cancel-check query throws on every activity.
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<SearchPipelineState>();
        var pipeServices = scope.ServiceProvider.GetRequiredService<SearchPipelineServices>();
        pipeServices.Agent = new AgentService(new FixedLlm(StrategyJson), new AgentOptions { DefaultGeo = "jordan" });
        state.Query = "tell me about tea";
        state.Geo = "jordan";
        state.ResultKey = "k1";
        state.SearchId = "12345"; // a real (parseable) job id, so the durable read is attempted and fails
        state.StartedAt = DateTimeOffset.UtcNow;

        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        var run = await runner.RunAsync(new SearchWorkflow(), cancellationToken: default);

        // The cooperative check swallowed its DB failure, so the first activity's body executed: the planner
        // was invoked and produced a strategy. A run is no longer sunk by a failing durable cancel read.
        state.Strategy.Should().NotBeNull(
            "a failed best-effort cancel read must degrade to 'keep running', never fault the search");
    }

    private sealed class ThrowingLlm : ILlmClient
    {
        public string Provider => "throwing";
        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default) =>
            throw new InvalidOperationException("The agent must not be called once a search is cancelled.");
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
