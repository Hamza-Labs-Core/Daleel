# Search Object & Result Grid Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The parsed query becomes a structured search object (`SearchStrategy` gains Product/Specs/Location/Goal/Facets/DefaultSort) that drives the grid: goal-driven default sort (+ rating), product-type facet filters, and per-card review signals.

**Architecture:** Extend the existing planner LLM call to emit the new fields; attach the strategy to `ProductSearchResult` (which serializes inside `AgentAnswer` → `SearchJob.ResultJson` and is what the grid binds). Three new pure, unit-testable helpers (`SortResolver`, `FacetBuilder`, `StoreReviewMatcher`) feed `ProductListings.razor` and a new `ReviewSignal.razor`. Everything is additive: missing metadata ⇒ today's behavior.

**Tech Stack:** .NET 8, Blazor Server + MudBlazor 8, xUnit + FluentAssertions + bUnit, System.Text.Json. CI builds `-c Release -warnaserror` (MudBlazor analyzer MUD0002 = error; always build Release before pushing razor).

**Spec:** `docs/superpowers/specs/2026-07-14-search-object-and-grid-design.md`

**Worktree:** `/Users/mahmouddarwish/Code/Daleel/.claude/worktrees/search-object`, branch `feat/search-object-and-grid`. All commands below run from this directory.

**Localization invariant:** every new `L["Key"]` needs the key in BOTH `src/Daleel.Web/Resources/SharedResource.resx` AND `SharedResource.ar.resx` (parity is required).

---

### Task 1: `SearchStrategy` gains the search-object fields + `SearchFacet`

**Files:**
- Modify: `src/Daleel.Core/Models/QueryPlan.cs`
- Test: `tests/Daleel.Core.Tests/SearchStrategyTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/Daleel.Core.Tests/SearchStrategyTests.cs`:

```csharp
using System.Text.Json;
using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests;

/// <summary>
/// The search-object fields on SearchStrategy: defaults are empty (fully additive), and the
/// record round-trips through System.Text.Json — it is persisted inside SearchJob.ResultJson,
/// so old JSON without the new fields MUST deserialize to the same empty defaults.
/// </summary>
public class SearchStrategyTests
{
    [Fact]
    public void NewFields_DefaultToEmpty()
    {
        var s = new SearchStrategy();
        s.Product.Should().BeEmpty();
        s.Specs.Should().BeEmpty();
        s.Location.Should().BeEmpty();
        s.Goal.Should().BeEmpty();
        s.Facets.Should().BeEmpty();
        s.DefaultSort.Should().BeEmpty();
    }

    [Fact]
    public void RoundTrips_ThroughJson()
    {
        var s = new SearchStrategy
        {
            Product = "diapers",
            Specs = new Dictionary<string, string> { ["size"] = "4" },
            Location = "Amman",
            Goal = "cheapest",
            DefaultSort = "price_asc",
            Facets = new[]
            {
                new SearchFacet { Key = "size", Label = "Size", Unit = null, Values = new[] { "3", "4", "5" } }
            }
        };

        var back = JsonSerializer.Deserialize<SearchStrategy>(JsonSerializer.Serialize(s))!;
        back.Product.Should().Be("diapers");
        back.Specs["size"].Should().Be("4");
        back.Goal.Should().Be("cheapest");
        back.DefaultSort.Should().Be("price_asc");
        back.Facets.Should().ContainSingle(f => f.Key == "size" && f.Values.Count == 3);
    }

    [Fact]
    public void OldJson_WithoutNewFields_DeserializesToEmptyDefaults()
    {
        const string oldJson = """{ "QueryType": 0, "Subject": "AC", "WebQueries": ["best AC"] }""";
        var s = JsonSerializer.Deserialize<SearchStrategy>(oldJson)!;
        s.Subject.Should().Be("AC");
        s.Product.Should().BeEmpty();
        s.Facets.Should().BeEmpty();
        s.DefaultSort.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daleel.Core.Tests/Daleel.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SearchStrategyTests" 2>&1 | tail -5`
Expected: FAIL to compile — `'SearchStrategy' does not contain a definition for 'Product'` / `SearchFacet` not found.

- [ ] **Step 3: Add the fields**

In `src/Daleel.Core/Models/QueryPlan.cs`, inside `public record SearchStrategy`, after the `Reasoning` property, add:

```csharp
    // ── Structured query metadata (the "search object" fields) ─────────────────
    // All optional with empty defaults: a planner that omits them leaves the grid on its generic
    // behavior, and old persisted ResultJson (which lacks them) deserializes unchanged.

    /// <summary>The actual product the user wants ("diapers"), distinct from the free-text Subject.</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>Constraints stated IN the query ("size"→"4", "color"→"white") — not per-result specs.</summary>
    public IReadOnlyDictionary<string, string> Specs { get; init; } = new Dictionary<string, string>();

    /// <summary>The place/market scope the user named ("Amman"), when any.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>The user's goal as free text ("cheapest", "best for newborns"). Guides ranking.</summary>
    public string Goal { get; init; } = string.Empty;

    /// <summary>Filter dimensions relevant to this product type, named by the planner.</summary>
    public IReadOnlyList<SearchFacet> Facets { get; init; } = Array.Empty<SearchFacet>();

    /// <summary>The goal-driven default sort key ("price_asc", "rating", …); empty ⇒ resolver heuristics.</summary>
    public string DefaultSort { get; init; } = string.Empty;
```

And after the `SearchStrategy` record (top level in the same file), add:

```csharp
/// <summary>
/// One product-type-specific filter dimension for the result grid, named by the planner
/// ("Screen Size" for TVs, "Size" for diapers). <see cref="Key"/> binds to a
/// <c>ProductModel.Specs</c> key; <see cref="Values"/> are optional candidate options that keep
/// the facet useful when results carry sparse specs (options = union of these + result values).
/// </summary>
public record SearchFacet
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Unit { get; init; }
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Daleel.Core.Tests/Daleel.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SearchStrategyTests" 2>&1 | tail -3`
Expected: `Passed!` (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Daleel.Core/Models/QueryPlan.cs tests/Daleel.Core.Tests/SearchStrategyTests.cs
git commit -m "SearchStrategy carries the structured search object (product/specs/location/goal/facets/sort)"
```

---

### Task 2: Planner emits the new fields

**Files:**
- Modify: `src/Daleel.Agent/PromptTemplates.cs` (the `StrategySchema` const, ~line 70)
- Modify: `src/Daleel.Agent/AgentService.cs` (`StrategyDto` + `ToStrategy()`, ~line 635)
- Test: `tests/Daleel.Agent.Tests/AgentServiceTests.cs` (add tests)

- [ ] **Step 1: Write the failing tests**

In `tests/Daleel.Agent.Tests/AgentServiceTests.cs`, add inside the `AgentServiceTests` class:

```csharp
    [Fact]
    public async Task PlanAsync_ParsesSearchObjectFieldsFromLlmJson()
    {
        const string json = """
            {
              "queryType": "ProductResearch", "intent": "Product", "subject": "diapers",
              "webQueries": ["diapers Jordan"], "shoppingQueries": [], "socialQueries": [],
              "placesQueries": [], "urlsToRead": [], "reasoning": "diapers",
              "product": "diapers",
              "specs": { "size": "4" },
              "location": "Amman",
              "goal": "cheapest",
              "defaultSort": "price_asc",
              "facets": [
                { "key": "size", "label": "Size", "unit": null, "values": ["3", "4", "5", "6"] },
                { "key": "count", "label": "Pack count" }
              ]
            }
            """;
        var agent = new AgentService(new FakeLlmClient(_ => json));
        var s = await agent.PlanAsync("cheapest size 4 diapers in Amman");

        s.Product.Should().Be("diapers");
        s.Specs.Should().Contain(new KeyValuePair<string, string>("size", "4"));
        s.Location.Should().Be("Amman");
        s.Goal.Should().Be("cheapest");
        s.DefaultSort.Should().Be("price_asc");
        s.Facets.Should().HaveCount(2);
        s.Facets[0].Values.Should().Contain("4");
        s.Facets[1].Values.Should().BeEmpty(); // omitted values default to empty, never null
    }

    [Fact]
    public async Task PlanAsync_WithoutSearchObjectFields_LeavesThemEmpty()
    {
        // The pre-existing StrategyJson (top of this class) has none of the new fields.
        var agent = new AgentService(PlannerAndAnalyst());
        var s = await agent.PlanAsync("plan something");

        s.Product.Should().BeEmpty();
        s.Specs.Should().BeEmpty();
        s.Goal.Should().BeEmpty();
        s.Facets.Should().BeEmpty();
        s.DefaultSort.Should().BeEmpty();
    }
```

- [ ] **Step 2: Run tests to verify the first fails**

Run: `dotnet test tests/Daleel.Agent.Tests/Daleel.Agent.Tests.csproj -c Release --filter "FullyQualifiedName~PlanAsync_ParsesSearchObjectFields|FullyQualifiedName~PlanAsync_WithoutSearchObjectFields" 2>&1 | tail -5`
Expected: `PlanAsync_ParsesSearchObjectFieldsFromLlmJson` FAILS (fields empty — DTO ignores them); the second may already pass.

- [ ] **Step 3: Extend the DTO and mapping**

In `src/Daleel.Agent/AgentService.cs`, inside `private sealed class StrategyDto`, after the `Reasoning` property add:

```csharp
        [JsonPropertyName("product")] public string? Product { get; set; }
        [JsonPropertyName("specs")] public Dictionary<string, string>? Specs { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("goal")] public string? Goal { get; set; }
        [JsonPropertyName("defaultSort")] public string? DefaultSort { get; set; }
        [JsonPropertyName("facets")] public List<FacetDto>? Facets { get; set; }
```

In the same class, extend `ToStrategy()` — inside the object initializer, after `Reasoning = Reasoning` add:

```csharp
            Product = Product ?? string.Empty,
            Specs = Specs ?? new Dictionary<string, string>(),
            Location = Location ?? string.Empty,
            Goal = Goal ?? string.Empty,
            DefaultSort = DefaultSort ?? string.Empty,
            Facets = (Facets ?? new List<FacetDto>())
                .Where(f => !string.IsNullOrWhiteSpace(f.Key))
                .Select(f => new SearchFacet
                {
                    Key = f.Key!.Trim(),
                    Label = string.IsNullOrWhiteSpace(f.Label) ? f.Key!.Trim() : f.Label!.Trim(),
                    Unit = string.IsNullOrWhiteSpace(f.Unit) ? null : f.Unit!.Trim(),
                    Values = f.Values ?? new List<string>()
                })
                .ToList()
```

After the `StrategyDto` class (sibling, still inside `AgentService`), add:

```csharp
    /// <summary>Wire shape for one planner-named facet.</summary>
    private sealed class FacetDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        [JsonPropertyName("values")] public List<string>? Values { get; set; }
    }
```

- [ ] **Step 4: Extend the planner prompt**

In `src/Daleel.Agent/PromptTemplates.cs`, in the `StrategySchema` const, replace the line

```
          "reasoning": "one sentence on the plan"
        }
```

with

```
          "reasoning": "one sentence on the plan",
          "product": "the bare product/thing wanted, e.g. 'diapers' — no geo words, no qualifiers",
          "specs": { "constraints stated in the query, e.g. size, color, capacity": "value" },
          "location": "the city/place the user named, or '' if none",
          "goal": "the user's goal in their words: 'cheapest', 'best', 'most reliable', or '' if none",
          "defaultSort": "one of: relevance | price_asc | price_desc | rating | sellers — pick from the goal ('cheapest'→price_asc, 'best'/quality→rating); use 'relevance' when unsure",
          "facets": [ { "key": "the spec dimension shoppers filter THIS product type by, e.g. 'size' for diapers, 'screen size' for TVs", "label": "display label", "unit": "unit or null", "values": ["typical options a store would offer, e.g. sizes 1-6 for diapers"] } ]
        }
        Give 2-4 facets for a product query (the dimensions a shopper actually narrows by), each with
        its typical values in this market. For non-product queries use [] and leave product/goal empty.
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Daleel.Agent.Tests/Daleel.Agent.Tests.csproj -c Release 2>&1 | tail -3`
Expected: `Passed!` (all — the full suite guards regressions in other planner tests)

- [ ] **Step 6: Commit**

```bash
git add src/Daleel.Agent/AgentService.cs src/Daleel.Agent/PromptTemplates.cs tests/Daleel.Agent.Tests/AgentServiceTests.cs
git commit -m "Planner emits the search object: product, specs, location, goal, facets, default sort"
```

---

### Task 3: Persist & deliver — `ProductSearchResult.Strategy`

**Files:**
- Modify: `src/Daleel.Core/Models/ProductSearchResult.cs` (add property, after `Schema` ~line 71)
- Modify: `src/Daleel.Agent/AgentService.cs` (`AskAsync`, ~line 128: attach strategy to products)
- Modify: `src/Daleel.Web/Pipeline/SearchActivities.cs` (~line 601: attach in the pipeline path)
- Test: `tests/Daleel.Agent.Tests/AgentServiceTests.cs`

- [ ] **Step 1: Write the failing test**

In `tests/Daleel.Agent.Tests/AgentServiceTests.cs` add:

```csharp
    [Fact]
    public async Task AskAsync_ProductQuery_CarriesStrategyOnProducts()
    {
        // PlannerAndAnalyst answers the planner call with StrategyJson (ProductResearch) and
        // everything else with plain text — enough for AskAsync to build a Products result.
        var agent = new AgentService(PlannerAndAnalyst());
        var answer = await agent.AskAsync("best AC in Jordan", "jordan");

        answer.Products.Should().NotBeNull();
        answer.Products!.Strategy.Should().NotBeNull();
        answer.Products.Strategy!.Subject.Should().Be("مكيف"); // from StrategyJson
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Daleel.Agent.Tests/Daleel.Agent.Tests.csproj -c Release --filter "FullyQualifiedName~AskAsync_ProductQuery_CarriesStrategy" 2>&1 | tail -5`
Expected: FAIL to compile — `'ProductSearchResult' does not contain a definition for 'Strategy'`.

- [ ] **Step 3: Add the property and wire both construction paths**

In `src/Daleel.Core/Models/ProductSearchResult.cs`, after the `Schema` property (~line 71), add:

```csharp
    /// <summary>
    /// The search object this result answers — the planner's structured understanding of the query
    /// (product, stated specs, location, goal, facet dimensions, default sort). Persisted with the
    /// result (this record serializes inside SearchJob.ResultJson) and read by the grid to drive
    /// per-type filters and goal-driven sorting. Null on results captured before this existed.
    /// </summary>
    public SearchStrategy? Strategy { get; init; }
```

In `src/Daleel.Agent/AgentService.cs` `AskAsync` (~line 128), replace:

```csharp
            products = await BuildProductSearchResultAsync(strategy.Subject is { Length: > 0 } s ? s : question,
                geo, bundle, summary, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
            products = await BuildProductSearchResultAsync(strategy.Subject is { Length: > 0 } s ? s : question,
                geo, bundle, summary, cancellationToken).ConfigureAwait(false);
            // Carry the search object on the result: it serializes inside ResultJson (persistence)
            // and the grid binds this exact record (delivery) — one write serves both.
            products = products with { Strategy = strategy };
```

In `src/Daleel.Web/Pipeline/SearchActivities.cs` (~line 601), in the `state.Answer = new AgentAnswer { … }` initializer, replace:

```csharp
            Products = state.Products,
```

with:

```csharp
            // Attach the search object so it persists in ResultJson and reaches the grid. state.Products
            // is built stepwise (extract → enrich) without the strategy; stamping it here, where the
            // final answer is assembled, catches every path.
            Products = state.Products is { } prod ? prod with { Strategy = state.Strategy } : null,
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Daleel.Agent.Tests/Daleel.Agent.Tests.csproj -c Release 2>&1 | tail -3`
Expected: `Passed!`

Run: `dotnet build src/Daleel.Web/Daleel.Web.csproj -c Release -warnaserror 2>&1 | tail -3`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add src/Daleel.Core/Models/ProductSearchResult.cs src/Daleel.Agent/AgentService.cs src/Daleel.Web/Pipeline/SearchActivities.cs tests/Daleel.Agent.Tests/AgentServiceTests.cs
git commit -m "Attach the search object to ProductSearchResult (persists in ResultJson, reaches the grid)"
```

---

### Task 4: `SortResolver` — goal → default sort key

**Files:**
- Create: `src/Daleel.Web/Services/SortResolver.cs`
- Test: `tests/Daleel.Web.Tests/SortResolverTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `tests/Daleel.Web.Tests/SortResolverTests.cs`:

```csharp
using Daleel.Core.Models;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// The goal→sort resolution chain: explicit DefaultSort wins when it's a known key; otherwise a
/// keyword heuristic over the free-text Goal; otherwise "relevance". Unknown/junk never leaks
/// through to the grid's sort switch.
/// </summary>
public class SortResolverTests
{
    [Theory]
    [InlineData("price_asc", "whatever", "price_asc")]   // explicit known key wins
    [InlineData("rating", "", "rating")]
    [InlineData("PRICE_DESC", "", "price_desc")]         // case-insensitive
    public void ExplicitKnownDefaultSort_Wins(string defaultSort, string goal, string expected) =>
        SortResolver.Resolve(new SearchStrategy { DefaultSort = defaultSort, Goal = goal })
            .Should().Be(expected);

    [Theory]
    [InlineData("cheapest", "price_asc")]
    [InlineData("lowest price", "price_asc")]
    [InlineData("best", "rating")]
    [InlineData("best for newborns", "rating")]
    [InlineData("top rated", "rating")]
    [InlineData("highest quality", "rating")]
    [InlineData("most expensive", "price_desc")]
    [InlineData("premium", "price_desc")]
    public void GoalKeywordHeuristic_WhenDefaultSortMissing(string goal, string expected) =>
        SortResolver.Resolve(new SearchStrategy { Goal = goal }).Should().Be(expected);

    [Theory]
    [InlineData("bogus_sort", "banana")] // unknown key + unmatchable goal
    [InlineData("", "")]
    public void FallsBackToRelevance(string defaultSort, string goal) =>
        SortResolver.Resolve(new SearchStrategy { DefaultSort = defaultSort, Goal = goal })
            .Should().Be("relevance");

    [Fact]
    public void NullStrategy_IsRelevance() => SortResolver.Resolve(null).Should().Be("relevance");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~SortResolverTests" 2>&1 | tail -5`
Expected: FAIL to compile — `SortResolver` not found.

- [ ] **Step 3: Implement**

Create `src/Daleel.Web/Services/SortResolver.cs`:

```csharp
using Daleel.Core.Models;

namespace Daleel.Web.Services;

/// <summary>
/// Resolves the grid's initial sort from the search object. Chain: the planner's explicit
/// <see cref="SearchStrategy.DefaultSort"/> when it names a KNOWN sort key → a keyword heuristic
/// over the free-text <see cref="SearchStrategy.Goal"/> → "relevance". Pure and total: any input,
/// including null, yields a key the grid's sort switch understands.
/// </summary>
public static class SortResolver
{
    /// <summary>Every sort key the grid understands (must match ProductListings' sort switch).</summary>
    private static readonly HashSet<string> KnownSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "relevance", "price_asc", "price_desc", "rating", "sellers"
    };

    public static string Resolve(SearchStrategy? strategy)
    {
        if (strategy is null)
        {
            return "relevance";
        }

        if (KnownSorts.Contains(strategy.DefaultSort))
        {
            return strategy.DefaultSort.ToLowerInvariant();
        }

        var goal = strategy.Goal.ToLowerInvariant();
        if (goal.Length == 0)
        {
            return "relevance";
        }

        // Order matters: "best price" should hit the price rule, so price keywords are checked
        // before the quality words that ride along in phrases like "best cheap option".
        if (ContainsAny(goal, "cheap", "lowest", "أرخص", "affordable", "budget"))
        {
            return "price_asc";
        }
        if (ContainsAny(goal, "expensive", "premium", "luxury", "high end", "high-end", "أغلى"))
        {
            return "price_desc";
        }
        if (ContainsAny(goal, "best", "top", "rated", "quality", "reliable", "أفضل"))
        {
            return "rating";
        }

        return "relevance";
    }

    private static bool ContainsAny(string goal, params string[] words) =>
        words.Any(w => goal.Contains(w, StringComparison.Ordinal));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~SortResolverTests" 2>&1 | tail -3`
Expected: `Passed!`

- [ ] **Step 5: Commit**

```bash
git add src/Daleel.Web/Services/SortResolver.cs tests/Daleel.Web.Tests/SortResolverTests.cs
git commit -m "SortResolver maps the search goal to the grid's default sort key"
```

---

### Task 5: Grid — `rating` sort + goal-driven default

**Files:**
- Modify: `src/Daleel.Web/Components/Shared/ProductListings.razor`
- Modify: `src/Daleel.Web/Resources/SharedResource.resx` + `SharedResource.ar.resx`
- Test: `tests/Daleel.Web.Tests/ProductListingsGridTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `tests/Daleel.Web.Tests/ProductListingsGridTests.cs`:

```csharp
using Bunit;
using Daleel.Core.Models;
using Daleel.Web.Components.Shared;
using Daleel.Web.Translation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// bUnit tests for the search-object-driven grid: goal-driven default sort (incl. the new
/// "rating" ordering) and clean fallback when the result carries no strategy.
/// Setup mirrors ComponentRenderTests (Mud services + localization + loose JS).
/// </summary>
public class ProductListingsGridTests : TestContext
{
    public ProductListingsGridTests()
    {
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddSingleton<ITranslationService>(new NoTranslation());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class NoTranslation : ITranslationService
    {
        public bool Enabled => false;
        public Task<string> TranslateAsync(string text, string targetLang, CancellationToken ct = default)
            => Task.FromResult(text);
        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
            => Task.FromResult(texts);
    }

    private static ProductModel Model(string name, decimal? price = null, double? rating = null, int? ratingCount = null) =>
        new()
        {
            Name = name,
            Brand = "B",
            Rating = rating,
            RatingCount = ratingCount,
            Offers = price is { } p
                ? new[] { new PriceOffer { Source = "s", Price = p, Currency = "JOD" } }
                : Array.Empty<PriceOffer>()
        };

    private static ProductSearchResult Result(SearchStrategy? strategy, params ProductModel[] models) =>
        new() { Query = "q", Geo = "jordan", Country = "Jordan", Models = models, Strategy = strategy };

    private IRenderedComponent<ProductListings> Render(ProductSearchResult result) =>
        RenderComponent<ProductListings>(p => p.Add(x => x.Result, result));

    private static IReadOnlyList<string> CardOrder(IRenderedComponent<ProductListings> cut, params string[] names)
        => names.OrderBy(n => cut.Markup.IndexOf(n, StringComparison.Ordinal)).ToList();

    [Fact]
    public void DefaultSort_FromStrategy_OrdersByPriceAscending()
    {
        var cut = Render(Result(
            new SearchStrategy { DefaultSort = "price_asc" },
            Model("Expensive", 900m), Model("Cheap", 100m), Model("Middle", 400m)));

        CardOrder(cut, "Expensive", "Cheap", "Middle")
            .Should().ContainInOrder("Cheap", "Middle", "Expensive");
    }

    [Fact]
    public void RatingSort_OrdersByRatingDesc_NullsLast()
    {
        var cut = Render(Result(
            new SearchStrategy { Goal = "best" }, // heuristic → rating
            Model("Unrated", 100m),
            Model("FourStar", 100m, rating: 4.0, ratingCount: 10),
            Model("FiveStar", 100m, rating: 5.0, ratingCount: 3)));

        CardOrder(cut, "Unrated", "FourStar", "FiveStar")
            .Should().ContainInOrder("FiveStar", "FourStar", "Unrated");
    }

    [Fact]
    public void NoStrategy_KeepsRelevanceOrder()
    {
        var cut = Render(Result(null, Model("First", 900m), Model("Second", 100m)));
        // relevance = incoming order, so the pricier "First" must stay first.
        CardOrder(cut, "First", "Second").Should().ContainInOrder("First", "Second");
    }
}
```

- [ ] **Step 2: Run tests to verify the sort tests fail**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~ProductListingsGridTests" 2>&1 | tail -6`
Expected: `DefaultSort_FromStrategy…` and `RatingSort…` FAIL (sort stays relevance; rating unknown). `NoStrategy…` passes.

- [ ] **Step 3: Implement in `ProductListings.razor`**

(a) In `OnParametersSet()`, inside the `if (!ReferenceEquals(_lastResult, Result))` block, after `_page = InitialPage;` add:

```csharp
            // A genuinely NEW result seeds the sort from the search object's goal ("cheapest" →
            // price_asc). Only here — re-renders of the same result must not undo a user's choice.
            _sort = SortResolver.Resolve(Result.Strategy);
```

(b) In `Recompute()`, add a `rating` case to the sort switch, before the default arm:

```csharp
            "rating" => q.OrderByDescending(x => x.Model.Rating.HasValue)
                         .ThenByDescending(x => x.Model.Rating ?? 0)
                         .ThenByDescending(x => x.Model.RatingCount ?? 0),
```

(c) In the sort `MudSelect` markup (after the `sellers` item), add:

```razor
                <MudSelectItem T="string" Value="@("rating")">@L["Filter.TopRated"]</MudSelectItem>
```

(d) No `@using` needed: `_Imports.razor` already imports `Daleel.Web.Services`, so `SortResolver` resolves unqualified in razor files.

(e) resx — add to `src/Daleel.Web/Resources/SharedResource.resx` (next to the other `Filter.*` keys):

```xml
  <data name="Filter.TopRated" xml:space="preserve"><value>Top rated</value></data>
```

And to `SharedResource.ar.resx`:

```xml
  <data name="Filter.TopRated" xml:space="preserve"><value>الأعلى تقييماً</value></data>
```

- [ ] **Step 4: Run tests + Release build**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~ProductListingsGridTests" 2>&1 | tail -3`
Expected: `Passed!` (3 tests)

Run: `dotnet build src/Daleel.Web/Daleel.Web.csproj -c Release -warnaserror 2>&1 | tail -3`
Expected: `Build succeeded.` (razor + MudBlazor analyzer clean)

- [ ] **Step 5: Commit**

```bash
git add src/Daleel.Web/Components/Shared/ProductListings.razor src/Daleel.Web/Resources/SharedResource.resx src/Daleel.Web/Resources/SharedResource.ar.resx tests/Daleel.Web.Tests/ProductListingsGridTests.cs
git commit -m "Grid seeds its sort from the search goal and gains a top-rated ordering"
```

---

### Task 6: `FacetBuilder` — facet dimensions + option union

**Files:**
- Create: `src/Daleel.Web/Services/FacetBuilder.cs`
- Test: `tests/Daleel.Web.Tests/FacetBuilderTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `tests/Daleel.Web.Tests/FacetBuilderTests.cs`:

```csharp
using Daleel.Core.Intelligence;
using Daleel.Core.Models;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// FacetBuilder turns the search object's facet dimensions into renderable facets whose options
/// are the union of planner candidates and the values actually present in the results. Facets are
/// ALWAYS emitted (never hidden for sparseness); spec keys match loosely (case + separators).
/// </summary>
public class FacetBuilderTests
{
    private static ProductModel WithSpecs(params (string K, string V)[] specs) => new()
    {
        Name = "m",
        Specs = specs.ToDictionary(s => s.K, s => s.V)
    };

    [Fact]
    public void Options_AreUnionOfPlannerValuesAndResultSpecs()
    {
        var strategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "size", Label = "Size", Values = new[] { "3", "4" } } }
        };
        var models = new[] { WithSpecs(("Size", "5")), WithSpecs(("size", "4")) };

        var facets = FacetBuilder.Build(strategy, ProductSchema.General, models);

        facets.Should().ContainSingle();
        facets[0].Options.Should().BeEquivalentTo(new[] { "3", "4", "5" });
    }

    [Fact]
    public void SpecKeyMatching_IgnoresCaseAndSeparators()
    {
        var strategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "screen size", Label = "Screen size" } }
        };
        // Result specs use snake_case (the schema convention) and title case — both must bind.
        var models = new[] { WithSpecs(("screen_size", "55")), WithSpecs(("Screen Size", "65")) };

        var facets = FacetBuilder.Build(strategy, ProductSchema.General, models);
        facets[0].Options.Should().BeEquivalentTo(new[] { "55", "65" });
    }

    [Fact]
    public void EmptyFacet_IsStillEmitted()
    {
        var strategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "count", Label = "Pack count" } }
        };
        var facets = FacetBuilder.Build(strategy, ProductSchema.General, Array.Empty<ProductModel>());

        facets.Should().ContainSingle(); // always shown, even with zero options
        facets[0].Options.Should().BeEmpty();
    }

    [Fact]
    public void NoStrategyFacets_FallsBackToProductSchemaFields()
    {
        var schema = new ProductSchema
        {
            ProductType = "air conditioner",
            Fields = new[]
            {
                new SpecField { Key = "btu", Label = "BTU", Importance = SpecImportance.Key },
                new SpecField { Key = "noise_db", Label = "Noise", Importance = SpecImportance.Normal }
            }
        };
        var models = new[] { WithSpecs(("btu", "12000")), WithSpecs(("btu", "18000")) };

        var facets = FacetBuilder.Build(new SearchStrategy(), schema, models);

        facets.Should().HaveCount(2);
        facets[0].Key.Should().Be("btu"); // Key-importance fields lead
        facets[0].Options.Should().BeEquivalentTo(new[] { "12000", "18000" });
    }

    [Fact]
    public void NullStrategy_AndEmptySchema_YieldNoFacets() =>
        FacetBuilder.Build(null, ProductSchema.General, Array.Empty<ProductModel>()).Should().BeEmpty();

    [Fact]
    public void Matches_FiltersModelBySelectedValue()
    {
        var model = WithSpecs(("Size", " 4 "));
        FacetBuilder.Matches(model, "size", "4").Should().BeTrue();   // trim + case-insensitive
        FacetBuilder.Matches(model, "size", "5").Should().BeFalse();
        FacetBuilder.Matches(model, "missing", "4").Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~FacetBuilderTests" 2>&1 | tail -5`
Expected: FAIL to compile — `FacetBuilder` not found.

- [ ] **Step 3: Implement**

Create `src/Daleel.Web/Services/FacetBuilder.cs`:

```csharp
using Daleel.Core.Intelligence;
using Daleel.Core.Models;

namespace Daleel.Web.Services;

/// <summary>One renderable filter facet: the dimension plus its selectable options.</summary>
public sealed record FacetView(string Key, string Label, string? Unit, IReadOnlyList<string> Options);

/// <summary>
/// Builds the grid's product-type filter facets from the search object. Dimensions come from the
/// planner (<see cref="SearchStrategy.Facets"/>) — or, when the planner named none, from the
/// existing LLM category schema (<see cref="ProductSchema.Fields"/>, Key-importance first), so
/// per-type filters appear even for strategies that predate the facet fields. Options are the
/// union of planner candidate values and the values present in the results' specs. Facets are
/// ALWAYS emitted, never hidden for sparseness (an empty facet renders disabled, not absent).
/// Spec keys bind loosely — "screen size" matches "screen_size" and "Screen Size" — because the
/// planner, the schema, and per-store extraction each case/format keys their own way.
/// </summary>
public static class FacetBuilder
{
    public static IReadOnlyList<FacetView> Build(
        SearchStrategy? strategy, ProductSchema schema, IReadOnlyList<ProductModel> models)
    {
        var dimensions = strategy?.Facets is { Count: > 0 } named
            ? named
            : SchemaFallback(schema);
        if (dimensions.Count == 0)
        {
            return Array.Empty<FacetView>();
        }

        return dimensions.Select(f =>
        {
            var options = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in f.Values)
            {
                var t = v.Trim();
                if (t.Length > 0 && seen.Add(t))
                {
                    options.Add(t);
                }
            }
            foreach (var m in models)
            {
                if (SpecValue(m, f.Key) is { } v && seen.Add(v))
                {
                    options.Add(v);
                }
            }
            return new FacetView(f.Key, f.Label, f.Unit, options);
        }).ToList();
    }

    /// <summary>True when the model's spec for the facet key (loose match) equals the selected value.</summary>
    public static bool Matches(ProductModel model, string facetKey, string value) =>
        SpecValue(model, facetKey) is { } v && string.Equals(v, value.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The model's trimmed spec value under the loose-normalized facet key, else null.</summary>
    private static string? SpecValue(ProductModel model, string facetKey)
    {
        var wanted = Normalize(facetKey);
        foreach (var (k, v) in model.Specs)
        {
            if (Normalize(k) == wanted && !string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }
        return null;
    }

    /// <summary>Schema fields as facet dimensions, Key-importance first (the defining specs lead).</summary>
    private static IReadOnlyList<SearchFacet> SchemaFallback(ProductSchema schema) =>
        schema.Fields
            .OrderBy(f => f.Importance == SpecImportance.Key ? 0 : 1)
            .Select(f => new SearchFacet { Key = f.Key, Label = f.Label, Unit = f.Unit })
            .ToList();

    /// <summary>Case-fold and strip separators so "Screen Size" ≡ "screen_size" ≡ "screen-size".</summary>
    private static string Normalize(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~FacetBuilderTests" 2>&1 | tail -3`
Expected: `Passed!` (6 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Daleel.Web/Services/FacetBuilder.cs tests/Daleel.Web.Tests/FacetBuilderTests.cs
git commit -m "FacetBuilder: planner-named dimensions (schema fallback) with union options"
```

---

### Task 7: Grid renders facet filters

**Files:**
- Modify: `src/Daleel.Web/Components/Shared/ProductListings.razor`
- Modify: `src/Daleel.Web/Resources/SharedResource.resx` + `SharedResource.ar.resx`
- Test: `tests/Daleel.Web.Tests/ProductListingsGridTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

Add to `tests/Daleel.Web.Tests/ProductListingsGridTests.cs`:

```csharp
    private static ProductModel WithSpec(string name, string key, string value, decimal price = 100m)
    {
        var m = Model(name, price);
        return m with { Specs = new Dictionary<string, string> { [key] = value } };
    }

    [Fact]
    public void FacetControl_RendersWithUnionOptions()
    {
        var strategy = new SearchStrategy
        {
            Facets = new[] { new SearchFacet { Key = "size", Label = "Diaper Size", Values = new[] { "3" } } }
        };
        var cut = Render(Result(strategy, WithSpec("A", "Size", "4"), WithSpec("B", "size", "5")));

        cut.Markup.Should().Contain("Diaper Size"); // the facet control label renders
    }

    [Fact]
    public void FacetPreselection_FromQuerySpecs_FiltersTheGrid()
    {
        var strategy = new SearchStrategy
        {
            Specs = new Dictionary<string, string> { ["size"] = "4" }, // the user SAID size 4
            Facets = new[] { new SearchFacet { Key = "size", Label = "Size" } }
        };
        var cut = Render(Result(strategy,
            WithSpec("SizeFour", "Size", "4"),
            WithSpec("SizeFive", "Size", "5")));

        cut.Markup.Should().Contain("SizeFour");
        cut.Markup.Should().NotContain("SizeFive"); // filtered out by the pre-selected facet
    }

    [Fact]
    public void NoStrategy_RendersNoFacetControls_AndAllModels()
    {
        var cut = Render(Result(null, Model("One", 100m), Model("Two", 200m)));
        cut.Markup.Should().Contain("One");
        cut.Markup.Should().Contain("Two");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~ProductListingsGridTests" 2>&1 | tail -6`
Expected: `FacetControl_RendersWithUnionOptions` and `FacetPreselection…` FAIL. `NoStrategy…` passes.

- [ ] **Step 3: Implement in `ProductListings.razor`**

(a) In the `@code` block, add state after `_maxPrice`:

```csharp
    // Facet filters (the search object's product-type dimensions). _facets is rebuilt whenever a
    // NEW Result arrives; _facetSelections maps facet key → selected value (AllValue = no filter).
    private IReadOnlyList<FacetView> _facets = Array.Empty<FacetView>();
    private readonly Dictionary<string, string> _facetSelections = new(StringComparer.OrdinalIgnoreCase);
```

(b) In `OnParametersSet()`, inside the `if (!ReferenceEquals(_lastResult, Result))` block (after the `_sort` seeding from Task 5), add:

```csharp
            // Rebuild the facet set for the new result and pre-select the constraints the user put
            // IN the query ("size 4 diapers" opens with size=4 already applied).
            _facets = FacetBuilder.Build(Result.Strategy, Result.Schema, Result.Models);
            _facetSelections.Clear();
            foreach (var facet in _facets)
            {
                var stated = Result.Strategy?.Specs
                    .FirstOrDefault(kv => FacetBuilder.Matches(
                        new ProductModel { Name = "probe", Specs = new Dictionary<string, string> { [facet.Key] = kv.Value } },
                        facet.Key, kv.Value) || NormalizeKey(kv.Key) == NormalizeKey(facet.Key));
                _facetSelections[facet.Key] =
                    stated is { Key.Length: > 0 } kv2 && NormalizeKey(kv2.Key) == NormalizeKey(facet.Key)
                        ? kv2.Value.Trim()
                        : AllValue;
            }
```

And add the tiny helper to the `@code` block:

```csharp
    /// <summary>Same loose key normalization FacetBuilder uses (case + separators stripped).</summary>
    private static string NormalizeKey(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
```

(c) In `Recompute()`, after the `_condition` filter and before the price filters, add:

```csharp
        foreach (var (facetKey, selected) in _facetSelections)
        {
            if (selected != AllValue)
            {
                q = q.Where(x => FacetBuilder.Matches(x.Model, facetKey, selected));
            }
        }
```

(d) Add a facet-selection setter used by the markup:

```csharp
    private void SetFacet(string key, string value)
    {
        if (_facetSelections.TryGetValue(key, out var cur) && cur != value)
        {
            _facetSelections[key] = value;
            OnFilterChanged();
        }
    }
```

(e) In the markup, after the sort `MudItem` (`</MudItem>` of the Sort select) and before the price `MudItem`, add:

```razor
        @foreach (var facet in _facets)
        {
            <MudItem xs="6" sm="3" @key="facet.Key">
                <MudSelect T="string"
                           Value="@_facetSelections.GetValueOrDefault(facet.Key, AllValue)"
                           ValueChanged="@(v => SetFacet(facet.Key, v))"
                           Label="@(facet.Unit is { Length: > 0 } u ? $"{facet.Label} ({u})" : facet.Label)"
                           Dense="true" Margin="Margin.Dense" Disabled="@(facet.Options.Count == 0)">
                    <MudSelectItem T="string" Value="@AllValue">@L["ProductFilters.Any"]</MudSelectItem>
                    @foreach (var opt in facet.Options)
                    {
                        <MudSelectItem T="string" Value="@opt">@opt</MudSelectItem>
                    }
                </MudSelect>
            </MudItem>
        }
```

(No new resx keys: reuses `ProductFilters.Any`, and facet labels come localized from the planner.)

- [ ] **Step 4: Run tests + Release build**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~ProductListingsGridTests|FullyQualifiedName~FacetBuilderTests" 2>&1 | tail -3`
Expected: `Passed!`

Run: `dotnet build src/Daleel.Web/Daleel.Web.csproj -c Release -warnaserror 2>&1 | tail -3`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/Daleel.Web/Components/Shared/ProductListings.razor tests/Daleel.Web.Tests/ProductListingsGridTests.cs
git commit -m "Grid renders product-type facet filters, pre-selected from the query's stated specs"
```

---

### Task 8: `StoreReviewMatcher` — store reviews for a card

**Files:**
- Create: `src/Daleel.Web/Services/StoreReviewMatcher.cs`
- Test: `tests/Daleel.Web.Tests/StoreReviewMatcherTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `tests/Daleel.Web.Tests/StoreReviewMatcherTests.cs`:

```csharp
using Daleel.Core.Models;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// The fuzzy offer→store join behind per-card store reviews. No shared id exists, so it matches
/// normalized store names (offer Source/Seller vs StoreInfo.Name) with a website-domain fallback
/// (offer Url host vs store Url host). Unmatched stores contribute nothing — a clean miss.
/// </summary>
public class StoreReviewMatcherTests
{
    private static StoreInfo Store(string name, string? url = null, params string[] reviewTexts) => new()
    {
        Name = name,
        Url = url,
        Reviews = reviewTexts.Select(t => new StoreReview { Text = t }).ToList()
    };

    private static ProductModel WithOffers(params PriceOffer[] offers) =>
        new() { Name = "m", Offers = offers };

    [Fact]
    public void MatchesOfferSource_ToStoreName_CaseAndWhitespaceInsensitive()
    {
        var model = WithOffers(new PriceOffer { Source = "  SmartBuy " });
        var stores = new[] { Store("smartbuy", null, "great store") };

        StoreReviewMatcher.ReviewsFor(model, stores)
            .Should().ContainSingle(r => r.Text == "great store");
    }

    [Fact]
    public void FallsBackToDomainMatch_WhenNamesDiffer()
    {
        var model = WithOffers(new PriceOffer { Source = "Offer Src", Url = "https://www.mumz.io/p/123" });
        var stores = new[] { Store("Mumzworld", "https://mumz.io", "fast delivery") };

        StoreReviewMatcher.ReviewsFor(model, stores)
            .Should().ContainSingle(r => r.Text == "fast delivery");
    }

    [Fact]
    public void UnmatchedStore_ContributesNothing()
    {
        var model = WithOffers(new PriceOffer { Source = "Alpha", Url = "https://alpha.jo/x" });
        var stores = new[] { Store("Beta", "https://beta.jo", "irrelevant") };

        StoreReviewMatcher.ReviewsFor(model, stores).Should().BeEmpty();
    }

    [Fact]
    public void DeduplicatesAcrossMultipleMatchingOffers()
    {
        var model = WithOffers(
            new PriceOffer { Source = "Beta" },
            new PriceOffer { Seller = "beta" });
        var stores = new[] { Store("Beta", null, "one review") };

        StoreReviewMatcher.ReviewsFor(model, stores).Should().HaveCount(1);
    }
}
```

(`StoreReview.Text` verified at `src/Daleel.Core/Models/StoreModels.cs:62`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~StoreReviewMatcherTests" 2>&1 | tail -5`
Expected: FAIL to compile — `StoreReviewMatcher` not found.

- [ ] **Step 3: Implement**

Create `src/Daleel.Web/Services/StoreReviewMatcher.cs`:

```csharp
using Daleel.Core.Models;

namespace Daleel.Web.Services;

/// <summary>
/// Associates a product card with the reviews of the stores selling it. There is no shared id
/// between a <see cref="PriceOffer"/> and a <see cref="StoreInfo"/>, so this is a deliberate
/// best-effort fuzzy join: normalized store-name equality (offer Source/Seller vs store Name),
/// then website-domain equality (offer Url host vs store Url host). A store that matches nothing
/// simply contributes no reviews — misses are expected and never an error.
/// </summary>
public static class StoreReviewMatcher
{
    public static IReadOnlyList<StoreReview> ReviewsFor(ProductModel model, IReadOnlyList<StoreInfo> stores)
    {
        if (model.Offers.Count == 0 || stores.Count == 0)
        {
            return Array.Empty<StoreReview>();
        }

        var offerNames = model.Offers
            .SelectMany(o => new[] { o.Source, o.Seller })
            .Select(NormalizeName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var offerHosts = model.Offers
            .Select(o => HostOf(o.Url))
            .Where(h => h is not null)
            .Select(h => h!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var reviews = new List<StoreReview>();
        foreach (var store in stores)
        {
            if (store.Reviews.Count == 0)
            {
                continue;
            }

            var byName = offerNames.Contains(NormalizeName(store.Name));
            var byHost = HostOf(store.Url) is { } host && offerHosts.Contains(host);
            if (byName || byHost)
            {
                reviews.AddRange(store.Reviews);
            }
        }
        return reviews;
    }

    /// <summary>Case-fold and strip non-alphanumerics: "  SmartBuy " ≡ "smartbuy" ≡ "Smart-Buy".</summary>
    private static string NormalizeName(string? name) =>
        name is null ? string.Empty : new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The URL's registrable-ish host with any leading "www." stripped, else null.</summary>
    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host)
            : null;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~StoreReviewMatcherTests" 2>&1 | tail -3`
Expected: `Passed!` (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Daleel.Web/Services/StoreReviewMatcher.cs tests/Daleel.Web.Tests/StoreReviewMatcherTests.cs
git commit -m "StoreReviewMatcher: fuzzy name/domain join from a card's offers to store reviews"
```

---

### Task 9: `ReviewSignal` on each card

**Files:**
- Create: `src/Daleel.Web/Components/Shared/ReviewSignal.razor`
- Modify: `src/Daleel.Web/Components/Shared/ModelCard.razor` (~line 62: fold the rating row into the signal)
- Modify: `src/Daleel.Web/Components/Shared/ProductListings.razor` (~line 85: pass store reviews to `ModelCard`)
- Modify: `src/Daleel.Web/Resources/SharedResource.resx` + `SharedResource.ar.resx`
- Test: `tests/Daleel.Web.Tests/ReviewSignalTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `tests/Daleel.Web.Tests/ReviewSignalTests.cs`:

```csharp
using Bunit;
using Daleel.Core.Models;
using Daleel.Web.Components.Shared;
using Daleel.Web.Translation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// The per-card review signal: renders nothing without data, and shows rating + count + the
/// buyer/social/store reviews (with sentiment) when present. Same bUnit setup as the other
/// component render tests.
/// </summary>
public class ReviewSignalTests : TestContext
{
    public ReviewSignalTests()
    {
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddSingleton<ITranslationService>(new NoTranslation());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class NoTranslation : ITranslationService
    {
        public bool Enabled => false;
        public Task<string> TranslateAsync(string text, string targetLang, CancellationToken ct = default)
            => Task.FromResult(text);
        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
            => Task.FromResult(texts);
    }

    [Fact]
    public void NoReviewData_RendersNothing()
    {
        var cut = RenderComponent<ReviewSignal>(p => p
            .Add(x => x.Model, new ProductModel { Name = "bare" }));
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Rating_AndBuyerReview_Render()
    {
        var model = new ProductModel
        {
            Name = "m",
            Rating = 4.5,
            RatingCount = 12,
            RatedReviews = new[] { new ProductReview("Loved it", 5, "Aisha") }
        };
        var cut = RenderComponent<ReviewSignal>(p => p.Add(x => x.Model, model));

        cut.Markup.Should().Contain("4.5");
        cut.Markup.Should().Contain("Loved it");
    }

    [Fact]
    public void SocialQuote_FromBrandReputation_Renders()
    {
        var model = new ProductModel
        {
            Name = "m",
            BrandReputation = new BrandReputation
            {
                Social = new SocialProof
                {
                    Reviews = new[] { new UserReview { Quote = "الجودة ممتازة", Sentiment = Sentiment.Positive } }
                }
            }
        };
        var cut = RenderComponent<ReviewSignal>(p => p.Add(x => x.Model, model));
        cut.Markup.Should().Contain("الجودة ممتازة");
    }

    [Fact]
    public void StoreReviews_PassedIn_Render()
    {
        var cut = RenderComponent<ReviewSignal>(p => p
            .Add(x => x.Model, new ProductModel { Name = "m" })
            .Add(x => x.StoreReviews, new[] { new StoreReview { Text = "fast delivery" } }));
        cut.Markup.Should().Contain("fast delivery");
    }
}
```

(`BrandReputation.Social` is `{ get; init; }` — verified at `src/Daleel.Core/Models/BrandReputation.cs:48`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~ReviewSignalTests" 2>&1 | tail -5`
Expected: FAIL to compile — `ReviewSignal` not found.

- [ ] **Step 3: Create `ReviewSignal.razor`**

Create `src/Daleel.Web/Components/Shared/ReviewSignal.razor`:

```razor
@* Compact per-card review signal: rating + review count + sentiment, with source icons for the
   review kinds present (buyer/social/store), expanding to the actual reviews. Renders NOTHING
   when the card has no review data of any kind — no empty shells on sparse results. *@
@inject IStringLocalizer<SharedResource> L

@if (HasAny)
{
    <div class="mt-1">
        <div class="d-flex align-center gap-1" style="cursor:pointer;" @onclick="() => _open = !_open"
             title="@L["Reviews.Signal"]">
            @if (Model.Rating is { } rating)
            {
                <MudIcon Icon="@Icons.Material.Filled.Star" Size="Size.Small" Color="Color.Warning" />
                <MudText Typo="Typo.caption">@rating.ToString("0.0")@(Model.RatingCount is { } rc ? $" ({rc:n0})" : "")</MudText>
            }
            @if (TotalReviews > 0)
            {
                <MudChip T="string" Size="Size.Small" Variant="Variant.Text" Color="@SentimentColor">
                    @string.Format(L["Reviews.NReviews"], TotalReviews)
                </MudChip>
            }
            @if (BuyerReviews.Count > 0)
            {
                <MudIcon Icon="@Icons.Material.Filled.RateReview" Size="Size.Small" Title="@L["Reviews.FromBuyers"]" />
            }
            @if (SocialReviews.Count > 0)
            {
                <MudIcon Icon="@Icons.Material.Filled.Forum" Size="Size.Small" Title="@L["Reviews.FromSocial"]" />
            }
            @if (StoreReviews.Count > 0)
            {
                <MudIcon Icon="@Icons.Material.Filled.Storefront" Size="Size.Small" Title="@L["Reviews.FromStores"]" />
            }
        </div>

        <MudCollapse Expanded="_open">
            <div class="pa-2">
                @foreach (var r in BuyerReviews.Take(3))
                {
                    <MudText Typo="Typo.caption" Class="d-block mb-1" dir="@Catalog.Dir(r.Text)">
                        “@r.Text”@(r.Rating is { } br ? $" — {br:0.#}★" : "")@(r.Author is { Length: > 0 } a ? $" — {a}" : "")
                    </MudText>
                }
                @foreach (var r in SocialReviews.Take(3))
                {
                    <MudText Typo="Typo.caption" Class="d-block mb-1" dir="@Catalog.Dir(r.Quote)">
                        “@r.Quote”@(r.Source is { Length: > 0 } src ? $" — {src}" : "")
                    </MudText>
                }
                @foreach (var r in StoreReviews.Take(2))
                {
                    <MudText Typo="Typo.caption" Class="d-block mb-1" dir="@Catalog.Dir(r.Text)">
                        “@r.Text” — @L["Reviews.StoreReview"]
                    </MudText>
                }
            </div>
        </MudCollapse>
    </div>
}

@code {
    [Parameter, EditorRequired] public ProductModel Model { get; set; } = default!;

    /// <summary>Reviews of the stores selling this model (resolved by the host via StoreReviewMatcher).</summary>
    [Parameter] public IReadOnlyList<StoreReview> StoreReviews { get; set; } = Array.Empty<StoreReview>();

    private bool _open;

    private IReadOnlyList<ProductReview> BuyerReviews => Model.RatedReviews;
    private IReadOnlyList<UserReview> SocialReviews =>
        Model.BrandReputation?.Social?.Reviews ?? Array.Empty<UserReview>();

    private int TotalReviews => BuyerReviews.Count + SocialReviews.Count + StoreReviews.Count;

    private bool HasAny => Model.Rating is not null || TotalReviews > 0;

    /// <summary>Positive-majority social sentiment tints the chip; negative warns; neutral stays default.</summary>
    private Color SentimentColor
    {
        get
        {
            var social = Model.BrandReputation?.Social;
            if (social is not { HasReviews: true })
            {
                return Color.Default;
            }
            if (social.Positive > social.Negative)
            {
                return Color.Success;
            }
            return social.Negative > social.Positive ? Color.Error : Color.Default;
        }
    }
}
```

(`Catalog.Dir` lives in `Daleel.Web.Services` — already imported by `_Imports.razor`, so it resolves unqualified here.)

(b) In `src/Daleel.Web/Components/Shared/ModelCard.razor`: add the parameter (in `@code`):

```csharp
    /// <summary>Reviews of the stores selling this model, for the per-card review signal.</summary>
    [Parameter] public IReadOnlyList<StoreReview> StoreReviews { get; set; } = Array.Empty<StoreReview>();
```

Replace the existing rating block (~lines 62–71):

```razor
        @if (Model.Rating is { } rating)
        {
            @* Product-level buyer rating aggregated across this model's listings — distinct from the
               brand-reputation chip above (that's the BRAND in-market; this is THIS product). The
               tooltip is the user-visible disambiguation between the two star scores. *@
            <div class="d-flex align-center gap-1 mt-1" title="@L["ModelCard.ProductRating"]">
                <MudIcon Icon="@Icons.Material.Filled.Star" Size="Size.Small" Color="Color.Warning" />
                <MudText Typo="Typo.caption">@rating.ToString("0.0")@(Model.RatingCount is { } rc ? $" ({rc:n0})" : "")</MudText>
            </div>
        }
```

with:

```razor
        @* Rating + reviews folded into one signal: the product-level star score plus the actual
           buyer/social/store reviews behind it (expandable). Renders nothing without data. *@
        <ReviewSignal Model="Model" StoreReviews="StoreReviews" />
```

(c) In `src/Daleel.Web/Components/Shared/ProductListings.razor`, in the `ModelCard` usage (~line 85), add the parameter:

```razor
                <ModelCard Model="model"
                           StoreReviews="@StoreReviewMatcher.ReviewsFor(model, Result.Stores)"
                           Query="@Result.Query"
```

(d) resx — add to `SharedResource.resx`:

```xml
  <data name="Reviews.Signal" xml:space="preserve"><value>Ratings and reviews</value></data>
  <data name="Reviews.NReviews" xml:space="preserve"><value>{0} reviews</value></data>
  <data name="Reviews.FromBuyers" xml:space="preserve"><value>Buyer reviews</value></data>
  <data name="Reviews.FromSocial" xml:space="preserve"><value>Social media</value></data>
  <data name="Reviews.FromStores" xml:space="preserve"><value>Store reviews</value></data>
  <data name="Reviews.StoreReview" xml:space="preserve"><value>store review</value></data>
```

And to `SharedResource.ar.resx`:

```xml
  <data name="Reviews.Signal" xml:space="preserve"><value>التقييمات والمراجعات</value></data>
  <data name="Reviews.NReviews" xml:space="preserve"><value>{0} مراجعة</value></data>
  <data name="Reviews.FromBuyers" xml:space="preserve"><value>مراجعات المشترين</value></data>
  <data name="Reviews.FromSocial" xml:space="preserve"><value>وسائل التواصل</value></data>
  <data name="Reviews.FromStores" xml:space="preserve"><value>مراجعات المتاجر</value></data>
  <data name="Reviews.StoreReview" xml:space="preserve"><value>مراجعة متجر</value></data>
```

- [ ] **Step 4: Run tests + Release build**

Run: `dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release --filter "FullyQualifiedName~ReviewSignalTests" 2>&1 | tail -3`
Expected: `Passed!` (4 tests)

Run: `dotnet build src/Daleel.Web/Daleel.Web.csproj -c Release -warnaserror 2>&1 | tail -3`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/Daleel.Web/Components/Shared/ReviewSignal.razor src/Daleel.Web/Components/Shared/ModelCard.razor src/Daleel.Web/Components/Shared/ProductListings.razor src/Daleel.Web/Resources/SharedResource.resx src/Daleel.Web/Resources/SharedResource.ar.resx tests/Daleel.Web.Tests/ReviewSignalTests.cs
git commit -m "ReviewSignal: per-card buyer/social/store reviews with sentiment, folded over the rating row"
```

---

### Task 10: Full verification

**Files:** none new.

- [ ] **Step 1: Full test suites**

Run each (the CLI accepts one project per `dotnet test` invocation):

```bash
dotnet test tests/Daleel.Core.Tests/Daleel.Core.Tests.csproj -c Release 2>&1 | tail -2
dotnet test tests/Daleel.Agent.Tests/Daleel.Agent.Tests.csproj -c Release 2>&1 | tail -2
dotnet test tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj -c Release 2>&1 | tail -2
dotnet test tests/Daleel.Pipeline.Tests/Daleel.Pipeline.Tests.csproj -c Release 2>&1 | tail -2
```

Expected: `Passed!` on all four.

- [ ] **Step 2: Whole-solution Release build (deploy gate)**

Run: `dotnet build Daleel.sln -c Release -warnaserror 2>&1 | tail -4`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Verify no unstaged leftovers, push**

```bash
git status --short   # expect: clean
git push -u origin feat/search-object-and-grid
```

- [ ] **Step 4: Update the spec status line**

In `docs/superpowers/specs/2026-07-14-search-object-and-grid-design.md`, change
`**Status:** Approved design — ready for implementation planning` to
`**Status:** Implemented on feat/search-object-and-grid (see docs/superpowers/plans/2026-07-14-search-object-and-grid.md)`, then:

```bash
git add docs/superpowers/specs/2026-07-14-search-object-and-grid-design.md
git commit -m "Spec status: implemented"
git push
```

---

## Deviations & discovered constraints (log while executing)

- `ProductSchema`/`SpecField` (`src/Daleel.Core/Intelligence/ProductSchema.cs`) already carries LLM-named per-type spec dimensions; `FacetBuilder` uses it as the fallback dimension source (spec §4).
- Per-card social reviews live at `Model.BrandReputation.Social` (`SocialProof` of `UserReview`), NOT a direct `ProductModel.SocialProof` — the aggregate `Result.Social` is derived from these (`AgentService.Products.cs:326`).
- Verified during planning: `StoreReview.Text` (StoreModels.cs:62), `BrandReputation.Social` init property (BrandReputation.cs:48), `Daleel.Web.Services` imported by `_Imports.razor` (razor files use the helpers unqualified).
