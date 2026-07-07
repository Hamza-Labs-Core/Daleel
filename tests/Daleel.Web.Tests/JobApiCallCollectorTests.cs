using Daleel.Core.Observability;
using Daleel.Web.Conversation;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

public class JobApiCallCollectorTests
{
    private static ApiCall Call(decimal cost) => new()
    {
        Provider = "SerpAPI", Endpoint = "shopping", RequestSummary = "ACs", ResponseTimeMs = 300,
        Status = ApiCallStatus.Success, EstimatedCost = cost
    };

    [Fact]
    public void Records_AccumulatesCallsAndCost_AndStreamsProgress()
    {
        var lines = new List<string>();
        var collector = new JobApiCallCollector(lines.Add);

        collector.Record(Call(0.005m));
        collector.Record(Call(0.030m));

        collector.Calls.Should().HaveCount(2);
        collector.TotalCost.Should().Be(0.035m);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("SerpAPI").And.Contain("~$0.005").And.Contain("total ~$0.005");
    }

    [Fact]
    public void Record_MetersButNeverCancels_EvenAtHighSpend()
    {
        // Cost is meter-only (R1): the collector has no cancellation vector at all — a job runs to
        // completion regardless of spend, and the total simply accrues for post-hoc credit charging.
        var collector = new JobApiCallCollector(_ => { });

        for (var i = 0; i < 50; i++) collector.Record(Call(1m));

        collector.TotalCost.Should().Be(50m);
        collector.Calls.Should().HaveCount(50);
    }
}
