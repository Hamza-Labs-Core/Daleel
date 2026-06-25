using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Characterizes the contract the SearchWorkflow relies on: when the workflow is run in-process via
/// <see cref="IWorkflowRunner"/>, the activities can reach a scoped service through their
/// <see cref="ActivityExecutionContext"/> and mutations are observed back in the caller's scope.
/// This is what lets the pipeline carry heavy domain state (the agent answer, the bundle) in a
/// scoped state object rather than round-tripping it through Elsa's serialized WorkflowState.
/// </summary>
public class ElsaStateFlowTests
{
    [Fact]
    public async Task Activity_SharesScopedState_WithCallerScope()
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa => elsa.AddActivity<TouchStateActivity>());
        services.AddScoped<ScopedState>();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<ScopedState>();
        state.Value = "in";

        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(new Sequence { Activities = { new TouchStateActivity() } });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished);
        state.Value.Should().Be("in-touched", "the activity resolved the same scoped state instance");
    }

    private sealed class ScopedState
    {
        public string Value { get; set; } = "";
    }

    private sealed class TouchStateActivity : CodeActivity
    {
        protected override void Execute(ActivityExecutionContext context)
        {
            var state = context.GetRequiredService<ScopedState>();
            state.Value += "-touched";
        }
    }
}
