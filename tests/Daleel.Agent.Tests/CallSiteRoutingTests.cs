using Daleel.Agent.Instrumentation;
using Daleel.Agent.Llm;
using Daleel.Core.Llm;
using Daleel.Core.Observability;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

public class CallSiteRoutingTests
{
    /// <summary>A stub client that reports which model it was built for and counts its calls.</summary>
    private sealed class StubLlm : ILlmClient
    {
        private readonly string _model;
        public StubLlm(string model) => _model = model;
        public string Provider => "stub";
        public int Calls { get; private set; }

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LlmResponse { Content = "ok", Model = _model });
        }
    }

    private sealed class CapturingObserver : IApiCallObserver
    {
        public List<ApiCall> Calls { get; } = new();
        public void Record(ApiCall call) => Calls.Add(call);
    }

    [Fact]
    public void Scope_SetsAndRestoresCurrent_AcrossNesting()
    {
        LlmCallSiteScope.Current.Should().BeNull();
        using (LlmCallSiteScope.Enter(LlmCallSites.Extraction))
        {
            LlmCallSiteScope.Current.Should().Be("extraction");
            using (LlmCallSiteScope.Enter(LlmCallSites.Planner))
            {
                LlmCallSiteScope.Current.Should().Be("planner");
            }

            LlmCallSiteScope.Current.Should().Be("extraction");
        }

        LlmCallSiteScope.Current.Should().BeNull();
    }

    [Fact]
    public async Task Routing_PicksModelForCurrentCallSite()
    {
        var built = new List<string>();
        ILlmClient router = new RoutingLlmClient(
            modelForCallSite: cs => cs == "extraction" ? "modelX" : "modelY",
            clientForModel: m => { built.Add(m); return new StubLlm(m); },
            defaultModel: "fallback");

        using (LlmCallSiteScope.Enter(LlmCallSites.Extraction))
        {
            (await router.CompleteTextAsync("s", "u")).Should().Be("ok");
        }

        built.Should().ContainSingle().Which.Should().Be("modelX");
    }

    [Fact]
    public async Task Routing_UsesDefaultOutsideAnyScope()
    {
        var built = new List<string>();
        ILlmClient router = new RoutingLlmClient(
            _ => "unused", m => { built.Add(m); return new StubLlm(m); }, "fallback");

        await router.CompleteTextAsync("s", "u");

        built.Should().ContainSingle().Which.Should().Be("fallback");
    }

    [Fact]
    public async Task Routing_BuildsOneClientPerDistinctModel()
    {
        var built = new List<string>();
        ILlmClient router = new RoutingLlmClient(
            cs => cs, m => { built.Add(m); return new StubLlm(m); }, "fallback");

        using (LlmCallSiteScope.Enter("extraction")) { await router.CompleteTextAsync("s", "u"); }
        using (LlmCallSiteScope.Enter("extraction")) { await router.CompleteTextAsync("s", "u"); } // cached
        using (LlmCallSiteScope.Enter("planner")) { await router.CompleteTextAsync("s", "u"); }

        built.Should().BeEquivalentTo(new[] { "extraction", "planner" });
    }

    [Fact]
    public async Task Logging_StampsCallSiteFromScope()
    {
        var observer = new CapturingObserver();
        ILlmClient logged = new LoggingLlmClient(new StubLlm("m"), observer, new CostEstimator());

        using (LlmCallSiteScope.Enter(LlmCallSites.Extraction))
        {
            await logged.CompleteTextAsync("s", "u");
        }

        var call = observer.Calls.Should().ContainSingle().Subject;
        call.CallSite.Should().Be("extraction");
        call.Endpoint.Should().Be("chat:extraction");
    }

    [Fact]
    public async Task Logging_NoScope_IsPlainChat()
    {
        var observer = new CapturingObserver();
        ILlmClient logged = new LoggingLlmClient(new StubLlm("m"), observer, new CostEstimator());

        await logged.CompleteTextAsync("s", "u");

        var call = observer.Calls.Should().ContainSingle().Subject;
        call.CallSite.Should().BeNull();
        call.Endpoint.Should().Be("chat");
    }

    [Fact]
    public async Task RoutingThroughLogging_TagsBothCallSiteAndModel()
    {
        // End-to-end: the router selects the per-call-site model, and the per-model logging client tags
        // the call-site — so analytics can attribute this call to (extraction, claude-sonnet-5).
        var observer = new CapturingObserver();
        var estimator = new CostEstimator();
        ILlmClient router = new RoutingLlmClient(
            modelForCallSite: cs => cs == "extraction" ? "anthropic/claude-sonnet-5" : "other",
            clientForModel: m => new LoggingLlmClient(new StubLlm(m), observer, estimator),
            defaultModel: "fallback");

        using (LlmCallSiteScope.Enter(LlmCallSites.Extraction))
        {
            await router.CompleteTextAsync("s", "u");
        }

        var call = observer.Calls.Should().ContainSingle().Subject;
        call.CallSite.Should().Be("extraction");
        call.Model.Should().Be("anthropic/claude-sonnet-5");
    }
}
