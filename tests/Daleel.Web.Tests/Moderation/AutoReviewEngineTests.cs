using Daleel.Core.Llm;
using Daleel.Web.Data;
using Daleel.Web.Moderation;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Moderation;

/// <summary>
/// The dynamic feedback loop end-to-end against real PostgreSQL: LLM audits land as AutoRating,
/// repeated wrong keyword flags self-activate a term suppression, and the human veto holds.
/// </summary>
public sealed class AutoReviewEngineTests : IDisposable
{
    private readonly PostgresTestContext _ctx = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private (FilteredContentLogRepository Logs, ModerationRuleRepository Rules) Repos() =>
        (new FilteredContentLogRepository(_ctx.Db), new ModerationRuleRepository(_ctx.Db));

    private static FilteredContentLog KeywordFinding(string content, string rule = "بار") => new()
    {
        Category = "alcohol",
        Rule = rule,
        Kind = "SocialPost",
        Content = content,
        ContentHash = Guid.NewGuid().ToString("N"),
        DecisionSource = "keyword",
        ItemRemoved = true,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private async Task SeedAsync(params FilteredContentLog[] rows)
    {
        _ctx.Db.FilteredContentLogs.AddRange(rows);
        await _ctx.Db.SaveChangesAsync();
    }

    /// <summary>An auditor that judges every finding halal and blames the keyword.</summary>
    private static FakeLlm AllHalalAuditor(IEnumerable<long> ids) => new(
        "[" + string.Join(",", ids.Select(id =>
            $$"""{"id": {{id}}, "haram": false, "confidence": 0.9, "note": "dehumidifier ad", "wrong_keyword": true}""")) + "]");

    [Fact]
    public async Task ThreeWrongFlags_SameTerm_AutoActivatesSuppression()
    {
        await SeedAsync(
            KeywordFinding("جهاز سحب الرطوبة والغبار"),
            KeywordFinding("مروحة سقف مع غبار أقل"),
            KeywordFinding("منقي هواء ضد الغبار"));
        var ids = await _ctx.Db.FilteredContentLogs.Select(f => f.Id).ToListAsync();
        var (logs, rules) = Repos();

        var reviewed = await new AutoReviewEngine(NullLogger.Instance)
            .ReviewAsync(AllHalalAuditor(ids), logs, rules, _cache);

        reviewed.Should().Be(3);
        var rows = await _ctx.Db.FilteredContentLogs.AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(r => r.AutoRating == -1 && r.AutoReviewedAt != null);
        var rule = (await rules.ActiveRulesAsync()).Should().ContainSingle().Subject;
        rule.Kind.Should().Be("suppress-term");
        rule.Term.Should().Be("بار");
        rule.Language.Should().Be("ar");
        rule.Source.Should().Be("llm");
    }

    [Fact]
    public async Task TwoWrongFlags_NotEnoughConsensus_NoRule()
    {
        await SeedAsync(KeywordFinding("جهاز سحب الرطوبة"), KeywordFinding("منقي هواء"));
        var ids = await _ctx.Db.FilteredContentLogs.Select(f => f.Id).ToListAsync();
        var (logs, rules) = Repos();

        await new AutoReviewEngine(NullLogger.Instance)
            .ReviewAsync(AllHalalAuditor(ids), logs, rules, _cache);

        (await rules.ActiveRulesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AdminHaramRating_VetoesAutoSuppression()
    {
        var vetoed = KeywordFinding("متجر بار حقيقي يبيع الكحول");
        vetoed.Rating = 1; // admin: this one really was haram
        await SeedAsync(
            vetoed,
            KeywordFinding("جهاز سحب الرطوبة"),
            KeywordFinding("منقي هواء"),
            KeywordFinding("مروحة سقف"));
        var unrated = await _ctx.Db.FilteredContentLogs
            .Where(f => f.Rating == null).Select(f => f.Id).ToListAsync();
        var (logs, rules) = Repos();

        await new AutoReviewEngine(NullLogger.Instance)
            .ReviewAsync(AllHalalAuditor(unrated), logs, rules, _cache);

        (await rules.ActiveRulesAsync()).Should().BeEmpty("a human said the term catches real haram");
    }

    [Fact]
    public async Task HaramVerdicts_StoreAutoRating_NoSuppression()
    {
        await SeedAsync(KeywordFinding("متجر خمور فعلي", rule: "خمور"));
        var id = (await _ctx.Db.FilteredContentLogs.SingleAsync()).Id;
        var llm = new FakeLlm(
            $$"""[{"id": {{id}}, "haram": true, "confidence": 0.95, "note": "actual liquor store", "wrong_keyword": false}]""");
        var (logs, rules) = Repos();

        await new AutoReviewEngine(NullLogger.Instance).ReviewAsync(llm, logs, rules, _cache);

        var row = await _ctx.Db.FilteredContentLogs.AsNoTracking().SingleAsync();
        row.AutoRating.Should().Be(1);
        row.AutoReviewNote.Should().Be("actual liquor store");
        (await rules.ActiveRulesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GarbageAuditorResponse_DefersTheBatch()
    {
        await SeedAsync(KeywordFinding("أي محتوى"));
        var (logs, rules) = Repos();

        var reviewed = await new AutoReviewEngine(NullLogger.Instance)
            .ReviewAsync(new FakeLlm("all looks fine to me!"), logs, rules, _cache);

        reviewed.Should().Be(0);
        (await _ctx.Db.FilteredContentLogs.AsNoTracking().SingleAsync())
            .AutoReviewedAt.Should().BeNull("the batch must be retried next cycle, not stamped off garbage");
    }

    [Fact]
    public async Task RepeatedCycles_DoNotDuplicateTheRule()
    {
        await SeedAsync(
            KeywordFinding("جهاز ١"), KeywordFinding("جهاز ٢"), KeywordFinding("جهاز ٣"));
        var ids = await _ctx.Db.FilteredContentLogs.Select(f => f.Id).ToListAsync();
        var (logs, rules) = Repos();
        var engine = new AutoReviewEngine(NullLogger.Instance);
        await engine.ReviewAsync(AllHalalAuditor(ids), logs, rules, _cache);

        // A later batch with three MORE wrong flags for the same term must reuse the rule.
        await SeedAsync(
            KeywordFinding("جهاز ٤"), KeywordFinding("جهاز ٥"), KeywordFinding("جهاز ٦"));
        var newIds = await _ctx.Db.FilteredContentLogs
            .Where(f => f.AutoReviewedAt == null).Select(f => f.Id).ToListAsync();
        await engine.ReviewAsync(AllHalalAuditor(newIds), logs, rules, _cache);

        (await rules.ActiveRulesAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData("بار", "ar")]
    [InlineData("bar", "en")]
    [InlineData("البيره", "ar")]
    public void DetectLanguage_SplitsByArabicBlock(string term, string expected) =>
        AutoReviewEngine.DetectLanguage(term).Should().Be(expected);

    private sealed class FakeLlm : ILlmClient
    {
        private readonly string _response;
        public string Provider => "fake";
        public FakeLlm(string response) => _response = response;

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmResponse { Content = _response });
    }

    public void Dispose()
    {
        _cache.Dispose();
        _ctx.Dispose();
    }
}
