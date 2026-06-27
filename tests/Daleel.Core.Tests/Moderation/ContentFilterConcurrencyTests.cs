using Daleel.Core.Moderation;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Moderation;

/// <summary>
/// One <see cref="ContentFilter"/> is shared by reference across up to five parallel sub-workflows (the
/// halal moderation chokepoint). Its audit log must therefore tolerate concurrent writes: an unlocked
/// <c>List.Add</c> can corrupt the backing array, throw, or silently drop entries. These tests hammer the
/// filter from many threads and assert every removal is recorded exactly once.
/// </summary>
public class ContentFilterConcurrencyTests
{
    [Fact]
    public async Task FilterText_RecordsEveryRemoval_UnderConcurrency()
    {
        var filter = new ContentFilter(FilterStrictness.Strict);
        const int writers = 256;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
        {
            // "beer" trips the alcohol category → exactly one Record per call.
            filter.FilterText("a cold beer please").Should().BeNull();
        })));

        filter.AuditLog.Should().HaveCount(writers, "every concurrent removal must be recorded, none lost");
        filter.AuditDetails.Should().HaveCount(writers);
        filter.AuditDetails.Should().OnlyContain(d => d.Category == "alcohol");
    }

    [Fact]
    public async Task FilterResults_KeepsHalalAndRecordsHaram_UnderConcurrency()
    {
        var filter = new ContentFilter(FilterStrictness.Strict);
        const int batches = 64;
        const int haramPerBatch = 4; // "beer", "wine", "casino", "pork"
        var batch = new[] { "fresh apples", "beer crate", "olive oil", "wine bottle", "casino night", "pork chop" };

        var results = await Task.WhenAll(Enumerable.Range(0, batches).Select(_ => Task.Run(() =>
            filter.FilterResults(batch, s => s))));

        results.Should().OnlyContain(kept => kept.Count == batch.Length - haramPerBatch);
        filter.AuditLog.Should().HaveCount(batches * haramPerBatch,
            "no audit entries are lost or duplicated when batches are filtered in parallel");
    }
}
