using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// The post-aggregation relevance gate: deterministic shopping hits reach the product grid without
/// any LLM pass, so off-target items (a milk frother or a "slimming coffee" drink in a coffee-MAKER
/// search) survive unless gated. The LLM call itself is best-effort; these tests pin the PURE parts —
/// the prompt contract and the drop-verdict semantics (fail-open per item).
/// </summary>
public class RelevanceGateTests
{
    private static ProductModel M(string name) => new() { Name = name };

    private static readonly IReadOnlyList<ProductModel> Models = new[]
    {
        M("Ninja 12-Cup Programmable Coffee Maker"),
        M("Electric Milk Frother Handheld"),
        M("Longreen Xlim Express Coffee"),
        M("Cuisinart DCC-3200 Coffee Maker"),
    };

    [Fact]
    public void ApplyRelevanceVerdicts_DropsListedIndices()
    {
        var kept = AgentService.ApplyRelevanceVerdicts(Models, new[] { 1, 2 });

        kept.Should().HaveCount(2);
        kept.Select(m => m.Name).Should().BeEquivalentTo(new[]
        {
            "Ninja 12-Cup Programmable Coffee Maker",
            "Cuisinart DCC-3200 Coffee Maker",
        });
    }

    [Fact]
    public void ApplyRelevanceVerdicts_IgnoresOutOfRangeAndDuplicateIndices()
    {
        // A hallucinated index must not throw or shift which items get dropped.
        var kept = AgentService.ApplyRelevanceVerdicts(Models, new[] { -1, 1, 1, 99 });

        kept.Should().HaveCount(3);
        kept.Select(m => m.Name).Should().NotContain("Electric Milk Frother Handheld");
    }

    [Fact]
    public void ApplyRelevanceVerdicts_EmptyOrAllInvalid_KeepsEverything()
    {
        AgentService.ApplyRelevanceVerdicts(Models, Array.Empty<int>()).Should().HaveCount(4);
        AgentService.ApplyRelevanceVerdicts(Models, new[] { -5, 42 }).Should().HaveCount(4);
    }

    [Fact]
    public void RelevanceGatePrompt_AsksTheOneQuestionPerItem()
    {
        var prompt = PromptTemplates.RelevanceGate("coffee maker", new[] { "Ninja 12-Cup", "Milk Frother" });

        // The target type anchors the single per-item question.
        prompt.Should().Contain("coffee maker");
        // Items are numbered so the reply can reference them by index.
        prompt.Should().Contain("0. Ninja 12-Cup");
        prompt.Should().Contain("1. Milk Frother");
        // The reply shape is drop-only (unlisted items fail open / stay).
        prompt.Should().Contain("\"drop\"");
        prompt.Should().Contain("when unsure, do not list the item");
    }

    [Fact]
    public void RelevanceGateSystem_KeepsWhenUnsure_AndCarriesHalalGuard()
    {
        PromptTemplates.RelevanceGateSystem.Should().Contain("ITSELF an instance");
        PromptTemplates.RelevanceGateSystem.Should().Contain("keep it");
        // The gate is the first LLM pass deterministic shopping hits ever see, so it must carry the guard.
        PromptTemplates.RelevanceGateSystem.Should().Contain("halal-compliant");
    }
}
