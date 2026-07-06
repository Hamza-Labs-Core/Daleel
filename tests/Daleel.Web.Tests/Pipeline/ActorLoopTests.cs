using System.Text.Json;
using Daleel.Agent;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Web.Pipeline.Enrichment.Actor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Pins the LLM-actor loop's guarantees: it terminates on 'done', dispatches tools, ENFORCES the turn
/// and tool-call bounds (so one step can't drain the unit budget), recovers from unparsable turns, and
/// always yields a structured result on exhaustion. These are what keep the "LLM as actor" step bounded
/// and crash-safe inside the durable queue.
/// </summary>
public class ActorLoopTests
{
    private static readonly ActorTool Fetch = new("fetch_page", "fetch a URL");
    private static readonly ActorTool Search = new("web_search", "search the web");

    private static ActorLoop Loop() => new(NullLogger<ActorLoop>.Instance);
    private static AgentService Agent(ScriptedLlm llm) => new(llm);

    private static (int Calls, ActorToolDispatch Dispatch) Counter(string observation = "OK")
    {
        var box = new int[1];
        ActorToolDispatch d = (tool, args, ct) => { box[0]++; return Task.FromResult(observation); };
        return (box[0], d);
    }

    [Fact]
    public async Task Uses_a_tool_then_finishes_and_returns_the_done_result()
    {
        var llm = new ScriptedLlm(
            "{\"thought\":\"open it\",\"action\":\"fetch_page\",\"args\":{\"url\":\"https://x\"}}",
            "{\"thought\":\"done reading\",\"action\":\"done\",\"result\":{\"specs\":{\"watts\":\"1450\"}}}");
        var calls = 0;
        ActorToolDispatch dispatch = (t, a, ct) => { calls++; return Task.FromResult("15 bar pump, 1450W"); };

        var result = await Loop().RunAsync(Agent(llm), "GOAL: extract specs", "product X",
            new[] { Fetch }, dispatch, new ActorBounds(5, 6), default);

        result.Completed.Should().BeTrue();
        calls.Should().Be(1, "one fetch, then done");
        result.Result!.Value.GetProperty("specs").GetProperty("watts").GetString().Should().Be("1450");
    }

    [Fact]
    public async Task Tool_budget_is_enforced_and_forces_a_finish()
    {
        // MaxToolCalls = 1, but the model keeps asking to fetch. After the first, tools are refused.
        var llm = new ScriptedLlm(
            "{\"action\":\"fetch_page\",\"args\":{\"url\":\"https://a\"}}",
            "{\"action\":\"fetch_page\",\"args\":{\"url\":\"https://b\"}}",  // refused — no budget
            "{\"action\":\"done\",\"result\":{\"ok\":true}}");
        var calls = 0;
        ActorToolDispatch dispatch = (t, a, ct) => { calls++; return Task.FromResult("page"); };

        var result = await Loop().RunAsync(Agent(llm), "GOAL", "x",
            new[] { Fetch }, dispatch, new ActorBounds(5, 1), default);

        calls.Should().Be(1, "the tool-call cap is hard");
        result.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Turn_exhaustion_forces_a_final_done()
    {
        // The model never says done within MaxTurns; the forced-finish turn returns a result.
        var llm = new ScriptedLlm(
            "{\"action\":\"web_search\",\"args\":{\"query\":\"a\"}}",
            "{\"action\":\"web_search\",\"args\":{\"query\":\"b\"}}",
            "{\"action\":\"done\",\"result\":{\"salvaged\":true}}"); // returned on the forced-finish call
        ActorToolDispatch dispatch = (t, a, ct) => Task.FromResult("results");

        var result = await Loop().RunAsync(Agent(llm), "GOAL", "x",
            new[] { Search }, dispatch, new ActorBounds(2, 9), default);

        result.Completed.Should().BeTrue("a bounded run always yields its best result");
        result.Result!.Value.GetProperty("salvaged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Repeated_unparsable_turns_abandon_without_looping()
    {
        var llm = new ScriptedLlm("not json", "still not json", "not reached");
        ActorToolDispatch dispatch = (t, a, ct) => Task.FromResult("x");

        var result = await Loop().RunAsync(Agent(llm), "GOAL", "x",
            new[] { Fetch }, dispatch, new ActorBounds(6, 6), default);

        result.Completed.Should().BeFalse("two bad turns → give up, let the durable unit retry");
        result.Result.Should().BeNull();
    }

    [Fact]
    public async Task Single_turn_no_tools_judgment_returns_immediately()
    {
        // The VerifyPage shape: content already in hand, bounds (1,0) — one judgment, no tools.
        var llm = new ScriptedLlm("{\"action\":\"done\",\"result\":{\"related\":true,\"condition\":\"new\"}}");
        var calls = 0;
        ActorToolDispatch dispatch = (t, a, ct) => { calls++; return Task.FromResult(""); };

        var result = await Loop().RunAsync(Agent(llm), "Judge this page", "PAGE MARKDOWN...",
            Array.Empty<ActorTool>(), dispatch, new ActorBounds(1, 0), default);

        result.Completed.Should().BeTrue();
        calls.Should().Be(0, "no tools used");
        result.Result!.Value.GetProperty("related").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Tool_error_is_observed_not_thrown()
    {
        var llm = new ScriptedLlm(
            "{\"action\":\"fetch_page\",\"args\":{\"url\":\"https://dead\"}}",
            "{\"action\":\"done\",\"result\":{\"recovered\":true}}");
        ActorToolDispatch dispatch = (t, a, ct) => throw new InvalidOperationException("DNS fail");

        var result = await Loop().RunAsync(Agent(llm), "GOAL", "x",
            new[] { Fetch }, dispatch, new ActorBounds(5, 6), default);

        result.Completed.Should().BeTrue("a failed tool becomes an observation the actor recovers from");
        result.Result!.Value.GetProperty("recovered").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_from_the_budget_token_propagates()
    {
        var llm = new ScriptedLlm("{\"action\":\"fetch_page\",\"args\":{}}", "{\"action\":\"done\",\"result\":{}}");
        ActorToolDispatch dispatch = (t, a, ct) => throw new OperationCanceledException();

        var act = async () => await Loop().RunAsync(Agent(llm), "GOAL", "x",
            new[] { Fetch }, dispatch, new ActorBounds(5, 6), default);

        await act.Should().ThrowAsync<OperationCanceledException>("lease/cost-cap cancellation is not swallowed");
    }

    [Fact]
    public async Task Item_dive_actor_drives_the_loop_and_extracts_specs()
    {
        // The actor "opens" a page (tool observation is empty here — providers aren't wired in the test),
        // then finishes with the specs it read. Exercises ItemDiveActor.RunAsync + parse end to end.
        var llm = new ScriptedLlm(
            "{\"thought\":\"open the official page\",\"action\":\"fetch_page\",\"args\":{\"url\":\"https://delonghi/ec685\"}}",
            "{\"thought\":\"read specs\",\"action\":\"done\",\"result\":{\"confirmedSku\":true," +
                "\"specs\":{\"pressure\":\"15 bar\",\"power\":\"1450W\"},\"sourceUrl\":\"https://delonghi/ec685\",\"note\":\"official page\"}}");
        var actor = new ItemDiveActor(Loop());
        var model = new ProductModel
        {
            Name = "DeLonghi Dedica EC685", Brand = "DeLonghi", Model = "EC685",
            Offers = new[] { new PriceOffer { Url = "https://store/ec685", Source = "Store" } }
        };

        var res = await actor.RunAsync(Agent(llm), model, "jordan", default);

        res.Should().NotBeNull("the actor returned a done result");
        res!.Specs.Should().ContainKeys("pressure", "power");
        res.ConfirmedSku.Should().BeTrue();
        res.SourceUrl.Should().Be("https://delonghi/ec685");
    }

    /// <summary>An ILlmClient that replays a fixed script of completions, one per call.</summary>
    private sealed class ScriptedLlm : ILlmClient
    {
        private readonly Queue<string> _script;
        public ScriptedLlm(params string[] turns) => _script = new Queue<string>(turns);
        public string Provider => "scripted";

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
        {
            var content = _script.Count > 0 ? _script.Dequeue() : "{\"action\":\"done\",\"result\":{}}";
            return Task.FromResult(new LlmResponse { Content = content });
        }
    }
}
