using System.Text.RegularExpressions;
using Daleel.Agent;
using Daleel.Agent.Instrumentation;
using Daleel.Core.Llm;
using Daleel.Core.Models;
using Daleel.Core.Observability;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using Daleel.Web.Pipeline.Enrichment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Pins the synthesis unit's contract: it settle-gates on the queue's OpenCount, makes a FIXED three
/// batched LLM calls regardless of grid size, writes each reduction onto an existing summary field,
/// is idempotent via the per-entity high-water mark, and fails soft (junk in → nothing written, no
/// retry). These are the guarantees that keep it cheap and crash-safe for a credit-billed owner.
/// </summary>
public class SynthesisTests
{
    private static readonly string ProductId =
        new ProductModel { Name = "DeLonghi Dedica EC685", Brand = "DeLonghi", Model = "EC685" }.Id;

    private static AgentAnswer Answer(int products = 1, int brands = 1, string summary = "placeholder")
    {
        var models = Enumerable.Range(0, products).Select(i => new ProductModel
        {
            Name = i == 0 ? "DeLonghi Dedica EC685" : $"Model {i}",
            Brand = i == 0 ? "DeLonghi" : $"Brand{i}",
            Model = i == 0 ? "EC685" : $"M{i}",
            Offers = new[] { new PriceOffer { Source = "Amazon", Price = 175m, Currency = "JOD", Condition = "used" } }
        }).ToList();

        var brandInfos = Enumerable.Range(0, brands)
            .Select(i => new BrandInfo { Name = i == 0 ? "DeLonghi" : $"Brand{i}" })
            .ToList();

        return new AgentAnswer
        {
            Question = "best espresso machine",
            Geo = "jordan",
            Products = new ProductSearchResult { Summary = summary, Models = models, Brands = brandInfos }
        };
    }

    private interface ICountedLlm : ILlmClient { int Calls { get; } }

    private static (SynthesisHandler Handler, EnrichmentUnitContext Ctx, EnrichmentWorkItem Item,
        RecordingResultStore Store, ICountedLlm Llm, CountingQueue Queue)
        Build(AgentAnswer answer, int openCount = 1, int attempts = 1, ILlmClient? llm = null)
    {
        var store = new RecordingResultStore(answer);
        var queue = new CountingQueue(openCount);
        var effectiveLlm = llm ?? new CountingLlm();

        var services = new ServiceCollection();
        services.AddSingleton<IWorkContextStore>(new FakeWorkContextStore());
        services.AddSingleton<IBrandRepository>(new NullBrandRepo());
        var provider = services.BuildServiceProvider();

        var item = new EnrichmentWorkItem
        {
            Id = 1, SearchJobId = 1, UserId = "u1", Kind = EnrichmentUnit.Synthesize,
            MaxAttempts = 10, Attempts = attempts
        };
        var ctx = new EnrichmentUnitContext
        {
            Services = provider,
            Job = new SearchJob { Id = 1, Query = "best espresso machine", Geo = "jordan" },
            Agent = () => new AgentService(effectiveLlm),
            Results = store,
            Queue = queue
        };

        return (new SynthesisHandler(NullLogger<SynthesisHandler>.Instance), ctx, item, store,
            (effectiveLlm as ICountedLlm)!, queue);
    }

    [Fact]
    public async Task Settle_gate_retries_without_any_llm_call_while_work_is_open()
    {
        var (handler, ctx, item, _, llm, queue) = Build(Answer(), openCount: 3, attempts: 1);

        var outcome = await handler.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Retry>();
        ((UnitOutcome.Retry)outcome).Delay.Should().NotBeNull("settle retries back off, never hot-loop");
        llm.Calls.Should().Be(0, "no synthesis happens until the world settles");
        queue.OpenCountCalls.Should().Be(1);
    }

    [Fact]
    public async Task Last_attempt_synthesizes_best_effort_even_if_work_is_still_open()
    {
        // open > 1 but this is the final attempt — it must produce a summary rather than Retry-Dead.
        var (handler, ctx, item, store, llm, _) = Build(Answer(), openCount: 5, attempts: 9); // MaxAttempts 10

        var outcome = await handler.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        llm.Calls.Should().BeGreaterThan(0, "the last-chance path synthesizes best-effort");
        store.Current.Products!.Summary.Should().NotBe("placeholder");
    }

    [Fact]
    public async Task Cost_is_a_fixed_three_calls_regardless_of_grid_size()
    {
        var (handler, ctx, item, _, llm, _) = Build(Answer(products: 40, brands: 15), openCount: 1);

        await handler.ExecuteAsync(item, ctx, default);

        llm.Calls.Should().Be(3, "one batched call each for products, brands, and the search — size-independent");
    }

    [Fact]
    public async Task Search_overview_overwrites_the_placeholder_summary()
    {
        var (handler, ctx, item, store, _, _) = Build(Answer(summary: "placeholder"), openCount: 1);

        await handler.ExecuteAsync(item, ctx, default);

        store.Current.Products!.Summary.Should().Be(SearchOverview,
            "the settled overview must replace the base-run placeholder to reach the UI");
    }

    [Fact]
    public async Task Product_summary_lands_on_ReviewSummary_located_by_stable_id()
    {
        var (handler, ctx, item, store, _, _) = Build(Answer(products: 1), openCount: 1);

        await handler.ExecuteAsync(item, ctx, default);

        var model = store.Current.Products!.Models.Single(m => m.Id == ProductId);
        model.ReviewSummary.Should().Be("SUMMARY:" + ProductId);
    }

    [Fact]
    public async Task Brand_narrative_lands_on_reputation_summary()
    {
        var (handler, ctx, item, store, _, _) = Build(Answer(brands: 1), openCount: 1);

        await handler.ExecuteAsync(item, ctx, default);

        var brand = store.Current.Products!.Brands.Single(b => b.Name == "DeLonghi");
        brand.Reputation!.Summary.Should().Be("NARRATIVE:delonghi");
    }

    [Fact]
    public async Task Re_run_with_an_unchanged_ledger_re_bills_nothing()
    {
        var (handler, ctx, item, _, llm, _) = Build(Answer(products: 2, brands: 2), openCount: 1);

        await handler.ExecuteAsync(item, ctx, default);
        var afterFirst = llm.Calls;
        afterFirst.Should().Be(3);

        // Same context (the fake store kept the high-water marks) — everything is up to date now.
        await handler.ExecuteAsync(item, ctx, default);

        llm.Calls.Should().Be(afterFirst, "a re-run whose findings ledger hasn't grown makes zero LLM calls");
    }

    [Fact]
    public async Task Unrelated_specs_are_dropped_and_wrong_condition_is_corrected()
    {
        // The owner's live complaint: a NEW product shown as "used", with an unrelated spec attached.
        var model = new ProductModel
        {
            Name = "DeLonghi Dedica EC685", Brand = "DeLonghi", Model = "EC685",
            Specs = new Dictionary<string, string> { ["ram"] = "16GB", ["watts"] = "1450" },
            Offers = new[] { new PriceOffer { Source = "Store", Price = 175m, Currency = "JOD", Condition = "used" } }
        };
        var answer = new AgentAnswer
        {
            Question = "espresso", Geo = "jordan",
            Products = new ProductSearchResult { Summary = "placeholder", Models = new[] { model }, Brands = Array.Empty<BrandInfo>() }
        };
        var (handler, ctx, item, store, _, _) = Build(answer, openCount: 1, llm: new CorrectingLlm());

        await handler.ExecuteAsync(item, ctx, default);

        var fixedModel = store.Current.Products!.Models.Single();
        fixedModel.Specs.Should().NotContainKey("ram", "an unrelated spec is dropped");
        fixedModel.Specs.Should().ContainKey("watts", "a real spec is kept");
        fixedModel.Offers.Single().Condition.Should().Be("new", "the wrong 'used' label is corrected from the facts");
    }

    [Fact]
    public async Task Two_models_sharing_a_stable_id_do_not_throw()
    {
        // (Sony, "WH-1000") and (Sony, "WH1000") collapse to the same StableId (punctuation dropped) —
        // the aggregator keeps them distinct but ProductModel.Id is identical. Must not throw.
        var m1 = new ProductModel { Name = "Sony WH-1000", Brand = "Sony", Model = "WH-1000" };
        var m2 = new ProductModel { Name = "Sony WH1000", Brand = "Sony", Model = "WH1000" };
        m1.Id.Should().Be(m2.Id, "punctuation-only differences collapse to one StableId");

        var answer = new AgentAnswer
        {
            Question = "headphones", Geo = "jordan",
            Products = new ProductSearchResult
            {
                Summary = "placeholder",
                Models = new[] { m1, m2 },
                Brands = new[] { new BrandInfo { Name = "Sony" } }
            }
        };
        var (handler, ctx, item, store, _, _) = Build(answer, openCount: 1);

        var outcome = await handler.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>("duplicate StableIds must not fault the unit");
        store.Current.Products!.Models.First(m => m.Id == m1.Id).ReviewSummary.Should().NotBeNull();
    }

    [Fact]
    public async Task An_entity_the_llm_omits_is_marked_considered_and_not_re_billed()
    {
        // Run 1: the LLM returns a product summary for only the FIRST of two products. The omitted one
        // must be marked considered so a re-run (with an unchanged ledger) makes ZERO calls.
        var (handler, ctx, item, _, llm, _) = Build(Answer(products: 2, brands: 1), openCount: 1, llm: new PartialLlm());

        await handler.ExecuteAsync(item, ctx, default);
        llm.Calls.Should().Be(3, "products + brands + search, one batch each");

        await handler.ExecuteAsync(item, ctx, default);
        llm.Calls.Should().Be(3, "the omitted product was marked considered — a re-run re-bills nothing");
    }

    [Fact]
    public async Task Junk_llm_output_writes_nothing_and_does_not_retry()
    {
        var (handler, ctx, item, store, _, _) = Build(Answer(products: 1, brands: 1), openCount: 1, llm: new JunkLlm());

        var outcome = await handler.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>("fail-soft: junk parses to nothing, never re-burns the budget");
        store.Current.Products!.Models.Single().ReviewSummary.Should().BeNull();
        store.Current.Products!.Summary.Should().Be("placeholder", "search summary is only overwritten by a real narrative");
    }

    [Fact]
    public async Task Synthesize_call_lands_on_the_metered_path()
    {
        // The design's biggest risk: an UNMETERED LLM call. Prove SynthesizeAsync flows through the
        // service's _llm, so the enrichment consumer's LoggingLlmClient wrapper meters and bills it
        // exactly like every other pipeline call (the consumer wires the ambient collector into _llm).
        var collector = new JobApiCallCollector(_ => { });
        var metered = new LoggingLlmClient(new CountingLlm(), collector, new CostEstimator());
        var agent = new AgentService(metered);

        await agent.SynthesizeAsync("system", "user", default);

        collector.Calls.Should().NotBeEmpty("a synthesis completion must be recorded on the metered path");
    }

    [Fact]
    public async Task Non_product_answer_does_nothing()
    {
        var answer = new AgentAnswer { Question = "weather", Geo = "jordan", Products = null };
        var (handler, ctx, item, _, llm, _) = Build(answer, openCount: 1);

        var outcome = await handler.ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        llm.Calls.Should().Be(0);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Echoes the entity ids/keys it sees in the "### &lt;id&gt;" headers of the user prompt back as valid
    /// JSON, so its output always matches the handler's computed StableIds. Counts calls.
    /// </summary>
    private const string SearchOverview = "A solid range of espresso machines across price tiers.";

    private sealed class CountingLlm : ICountedLlm
    {
        public int Calls { get; private set; }
        public string Provider => "stub";

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            var user = messages[0].Content;
            var ids = Regex.Matches(user, @"^### (.+)$", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value.Trim()).ToList();

            string content;
            if (systemPrompt.Contains("product's per-source"))
            {
                content = "[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"summary\":\"SUMMARY:{id}\"}}")) + "]";
            }
            else if (systemPrompt.Contains("brand's facts"))
            {
                content = "[" + string.Join(",", ids.Select(k => $"{{\"nameKey\":\"{k}\",\"narrative\":\"NARRATIVE:{k}\"}}")) + "]";
            }
            else
            {
                content = "{\"overview\":\"" + SearchOverview + "\"}";
            }

            return Task.FromResult(new LlmResponse { Content = content });
        }
    }

    /// <summary>Returns a product summary for only the FIRST product; full brand/search — for the omitted-entity test.</summary>
    private sealed class PartialLlm : ICountedLlm
    {
        public int Calls { get; private set; }
        public string Provider => "stub";

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            var user = messages[0].Content;
            var ids = Regex.Matches(user, @"^### (.+)$", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value.Trim()).ToList();

            string content;
            if (systemPrompt.Contains("product's per-source"))
            {
                content = ids.Count == 0 ? "[]" : $"[{{\"id\":\"{ids[0]}\",\"summary\":\"SUMMARY:{ids[0]}\"}}]";
            }
            else if (systemPrompt.Contains("brand's facts"))
            {
                content = "[" + string.Join(",", ids.Select(k => $"{{\"nameKey\":\"{k}\",\"narrative\":\"NARRATIVE:{k}\"}}")) + "]";
            }
            else
            {
                content = "{\"overview\":\"" + SearchOverview + "\"}";
            }

            return Task.FromResult(new LlmResponse { Content = content });
        }
    }

    /// <summary>Returns corrections (drop "ram", set condition "new") for every product — for the reconcile test.</summary>
    private sealed class CorrectingLlm : ICountedLlm
    {
        public int Calls { get; private set; }
        public string Provider => "stub";

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            var ids = Regex.Matches(messages[0].Content, @"^### (.+)$", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value.Trim()).ToList();

            string content;
            if (systemPrompt.Contains("CORRECT obvious errors"))
            {
                content = "[" + string.Join(",", ids.Select(id =>
                    $"{{\"id\":\"{id}\",\"summary\":\"SUMMARY:{id}\",\"dropSpecs\":[\"ram\"],\"condition\":\"new\"}}")) + "]";
            }
            else if (systemPrompt.Contains("brand's facts"))
            {
                content = "[]";
            }
            else
            {
                content = "{\"overview\":\"" + SearchOverview + "\"}";
            }

            return Task.FromResult(new LlmResponse { Content = content });
        }
    }

    private sealed class JunkLlm : ILlmClient
    {
        public string Provider => "stub";
        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
        {
            // Product/brand: unparseable text ⇒ Deserialize yields null ⇒ nothing written. Search:
            // empty ⇒ nothing written. Together they exercise the fail-soft path on every scope.
            var isSearch = !systemPrompt.Contains("per-source") && !systemPrompt.Contains("brand's facts");
            return Task.FromResult(new LlmResponse
            {
                Content = isSearch ? string.Empty : "sorry, I can't produce that {{{ not json"
            });
        }
    }

    private sealed class RecordingResultStore : IEnrichedResultStore
    {
        public AgentAnswer Current { get; private set; }
        public RecordingResultStore(AgentAnswer answer) => Current = answer;

        public Task<AgentAnswer?> LoadAsync(int jobId, CancellationToken ct = default) =>
            Task.FromResult<AgentAnswer?>(Current);

        public Task<bool> PatchAsync(
            EnrichmentWorkItem item, Func<AgentAnswer, AgentAnswer?> mutate, CancellationToken ct = default)
        {
            if (mutate(Current) is { } patched)
            {
                Current = patched;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    private sealed class CountingQueue : IEnrichmentWorkQueue
    {
        private readonly int _open;
        public int OpenCountCalls { get; private set; }
        public CountingQueue(int open) => _open = open;

        public Task<int> OpenCountAsync(int searchJobId, CancellationToken ct = default)
        {
            OpenCountCalls++;
            return Task.FromResult(_open);
        }

        public Task<bool> AnyOfKindAsync(int searchJobId, string kind, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task EnqueueAsync(IReadOnlyList<EnrichmentWorkItem> items, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> EnqueueFanOutAsync(int j, string k, IReadOnlyList<EnrichmentWorkItem> c, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<EnrichmentWorkItem>> ClaimAsync(int max, TimeSpan lease, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EnrichmentWorkItem>>(Array.Empty<EnrichmentWorkItem>());
        public Task CompleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RetryAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequeueAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(long id, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> ReapExhaustedAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    /// <summary>In-memory work-context store: enough of the real semantics (HWM + findings) to drive the handler.</summary>
    private sealed class FakeWorkContextStore : IWorkContextStore
    {
        private readonly Dictionary<(int, string, string), WorkContext> _rows = new();

        private WorkContext Row(int j, string s, string k)
        {
            if (!_rows.TryGetValue((j, s, k), out var r))
            {
                r = new WorkContext { SearchJobId = j, Scope = s, Key = k, FindingsJson = "[]" };
                _rows[(j, s, k)] = r;
            }
            return r;
        }

        public Task AppendFindingAsync(int j, string s, string k, string step, string note, CancellationToken ct = default)
        {
            var r = Row(j, s, k);
            // Represent N findings as an N-element array so the handler's array-length count is right.
            var count = r.FindingsJson.Count(c => c == '{') + 1;
            r.FindingsJson = "[" + string.Join(",", Enumerable.Range(0, count).Select(_ => "{}")) + "]";
            return Task.CompletedTask;
        }

        public Task SetSynthesisAsync(int j, string s, string k, string synthesis, int folded, CancellationToken ct = default)
        {
            var r = Row(j, s, k);
            r.Synthesis = synthesis;
            r.SynthesizedFindingCount = folded;
            r.SynthesisVersion++;
            return Task.CompletedTask;
        }

        public Task MarkSynthesizedAsync(int j, string s, string k, int folded, CancellationToken ct = default)
        {
            var r = Row(j, s, k);
            if (folded > r.SynthesizedFindingCount) r.SynthesizedFindingCount = folded;
            return Task.CompletedTask;
        }

        public Task<WorkContext?> GetAsync(int j, string s, string k, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue((j, s, k), out var r) ? r : null);

        public Task<IReadOnlyList<WorkContext>> ListForJobAsync(int j, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkContext>>(_rows.Values.Where(r => r.SearchJobId == j).ToList());

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NullBrandRepo : IBrandRepository
    {
        public Task<Brand?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<Brand?>(null);
        public Task<Brand?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<Brand?>(null);
        public Task<Brand> UpsertAsync(Brand brand, CancellationToken ct = default) => Task.FromResult(brand);
        public Task<IReadOnlyList<Brand>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(Array.Empty<Brand>());
        public Task<IReadOnlyList<Brand>> ListStaleAsync(DateTimeOffset olderThan, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(Array.Empty<Brand>());
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<Brand>> SearchAsync(string? query, int skip, int take, string? category = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Brand>>(Array.Empty<Brand>());

        public Task<IReadOnlyList<string>> DistinctModelCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
