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
    public async Task Lenient_parse_accepts_tool_alias_and_top_level_args()
    {
        // Weak-model variations seen on QA: "tool" instead of "action", and the args at the top level
        // (no nested "args" object). The loop must still dispatch the tool with the right arguments.
        var llm = new ScriptedLlm(
            "{\"thought\":\"open it\",\"tool\":\"fetch_page\",\"url\":\"https://x\"}",
            "{\"action\":\"done\",\"result\":{\"ok\":true}}");
        string? seenUrl = null;
        ActorToolDispatch dispatch = (t, a, ct) =>
        {
            seenUrl = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("url", out var u) ? u.GetString() : null;
            return Task.FromResult("page");
        };

        var result = await Loop().RunAsync(Agent(llm), "GOAL", "x", new[] { Fetch }, dispatch, new ActorBounds(5, 6), default);

        result.Completed.Should().BeTrue();
        seenUrl.Should().Be("https://x", "the tool alias and top-level args are understood");
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

    [Fact]
    public async Task Verify_page_actor_judges_relatedness_price_and_condition()
    {
        var llm = new ScriptedLlm(
            "{\"action\":\"done\",\"result\":{" +
              "\"models\":[" +
                "{\"name\":\"DeLonghi Dedica EC685\",\"related\":true,\"price\":{\"value\":175.0,\"currency\":\"JOD\",\"exact\":true}}," +
                "{\"name\":\"Random Blender X\",\"related\":false,\"price\":null}]," +
              "\"condition\":\"new\",\"description\":\"A slim 15-bar espresso machine.\"}}");
        var actor = new VerifyPageActor(Loop());
        var named = new[]
        {
            new ProductModel { Name = "DeLonghi Dedica EC685", Brand = "DeLonghi", Model = "EC685" },
            new ProductModel { Name = "Random Blender X", Brand = "Acme" }
        };

        var j = await actor.JudgeAsync(Agent(llm), "PAGE MARKDOWN about the Dedica...", named, default);

        j.Should().NotBeNull();
        j!.Related.Should().Contain("DeLonghi Dedica EC685");
        j.Related.Should().NotContain("Random Blender X", "an unrelated model is not marked related");
        j.PricesByModel["DeLonghi Dedica EC685"]!.Value.Price.Should().Be(175.0m);
        j.Condition.Should().Be("new");
        j.Description.Should().Contain("15-bar");
    }

    [Fact]
    public async Task Brand_site_actor_returns_official_and_local_sites()
    {
        var llm = new ScriptedLlm(
            "{\"action\":\"web_search\",\"args\":{\"query\":\"DeLonghi official site Jordan\"}}",
            "{\"action\":\"done\",\"result\":{\"website\":\"https://www.delonghi.com\"," +
                "\"localUrl\":\"https://www.delonghi.com/en-jo\",\"regionalUrl\":null," +
                "\"description\":\"Italian small-appliance maker.\",\"social\":[\"https://instagram.com/delonghi\",\"not-a-url\"]}}");
        var actor = new BrandSiteActor(Loop());
        var geo = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault("jordan");

        var sites = await actor.FindAsync(Agent(llm), "DeLonghi", geo, productContext: null, default);

        sites.Should().NotBeNull();
        sites!.Website.Should().Be("https://www.delonghi.com");
        sites.LocalUrl.Should().Be("https://www.delonghi.com/en-jo");
        sites.Social.Should().ContainSingle().Which.Should().Be("https://instagram.com/delonghi");
        sites.Social.Should().NotContain("not-a-url", "non-URL social entries are dropped");
    }

    [Fact]
    public async Task Catalog_actor_filters_unrelated_discoveries()
    {
        var llm = new ScriptedLlm(
            "{\"action\":\"done\",\"result\":{\"related\":[\"DeLonghi Dedica EC685\",\"DeLonghi Magnifica\"]}}");
        var actor = new CatalogActor(Loop());
        var names = new[] { "DeLonghi Dedica EC685", "DeLonghi Magnifica", "Fondant Spray Gun", "Descaling Tablets" };

        var related = await actor.FilterRelatedAsync(Agent(llm), "espresso machine", names, default);

        related.Should().Contain("DeLonghi Dedica EC685").And.Contain("DeLonghi Magnifica");
        related.Should().NotContain("Fondant Spray Gun");
        related.Should().NotContain("Descaling Tablets");
    }

    [Fact]
    public async Task Catalog_actor_fails_open_keeping_all_on_bad_output()
    {
        var related = await new CatalogActor(Loop())
            .FilterRelatedAsync(Agent(new ScriptedLlm("garbage, not json")), "x", new[] { "A", "B" }, default);
        related.Should().BeEquivalentTo(new[] { "A", "B" }, "a bad/empty response must not strip the catalogue");
    }

    [Fact]
    public async Task Store_site_actor_returns_verified_url_or_null()
    {
        var found = new ScriptedLlm(
            "{\"action\":\"web_search\",\"args\":{\"query\":\"Smart Buy Jordan official site\"}}",
            "{\"action\":\"done\",\"result\":{\"website\":\"https://smartbuy-me.com/jo\"}}");
        var geo = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault("jordan");

        var url = await new StoreSiteActor(Loop()).FindSiteAsync(Agent(found), "Smart Buy", geo, default);
        url.Should().Be("https://smartbuy-me.com/jo");

        // A non-URL / null website ⇒ null: the store has no own site and the caller spends nothing
        // (a hostname is never fabricated from the store's name).
        var none = new ScriptedLlm("{\"action\":\"done\",\"result\":{\"website\":null}}");
        (await new StoreSiteActor(Loop()).FindSiteAsync(Agent(none), "No Site Store", geo, default)).Should().BeNull();
    }

    [Fact]
    public async Task Brand_site_actor_grounds_disambiguation_in_the_product_category()
    {
        // "Sharp" is a San Diego hospital chain AND the electronics maker; "Mini" is a car and an
        // appliance brand. With only the bare name the actor can't tell them apart and lands on
        // whatever the top web hit is (QA showed Sharp → Sharp HealthCare). The originating search's
        // product category MUST reach the actor's prompt so it seeks the RIGHT same-named entity.
        var capture = new CapturingLoop();
        var actor = new BrandSiteActor(capture);
        var geo = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault("jordan");

        await actor.FindAsync(
            Agent(new ScriptedLlm("{\"action\":\"done\",\"result\":{}}")),
            "Sharp", geo, productContext: "televisions", ct: default);

        (capture.System + "\n" + capture.Context).Should().Contain("televisions",
            "the actor must know which kind of 'Sharp' brand to look for");
    }

    /// <summary>Captures the guiding system + initial context the actor hands the loop (no LLM run).</summary>
    private sealed class CapturingLoop : IActorLoop
    {
        public string System = string.Empty;
        public string Context = string.Empty;

        public Task<ActorResult> RunAsync(
            AgentService agent, string guidingSystem, string initialContext,
            IReadOnlyList<ActorTool> tools, ActorToolDispatch dispatch, ActorBounds bounds, CancellationToken ct)
        {
            System = guidingSystem;
            Context = initialContext;
            return Task.FromResult(new ActorResult(false, null, new string[0]));
        }
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
