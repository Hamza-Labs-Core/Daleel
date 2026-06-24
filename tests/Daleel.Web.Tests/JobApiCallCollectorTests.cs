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
        var collector = new JobApiCallCollector(lines.Add, maxCost: 0, capTrip: null);

        collector.Record(Call(0.005m));
        collector.Record(Call(0.030m));

        collector.Calls.Should().HaveCount(2);
        collector.TotalCost.Should().Be(0.035m);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("SerpAPI").And.Contain("~$0.005").And.Contain("total ~$0.005");
    }

    [Fact]
    public void Cap_TripsCancellation_WhenExceeded()
    {
        using var cts = new CancellationTokenSource();
        var lines = new List<string>();
        var collector = new JobApiCallCollector(lines.Add, maxCost: 0.01m, capTrip: cts);

        collector.Record(Call(0.008m));
        cts.IsCancellationRequested.Should().BeFalse();

        collector.Record(Call(0.008m)); // total 0.016 > 0.01
        cts.IsCancellationRequested.Should().BeTrue();
        lines.Should().Contain(l => l.Contains("Cost cap"));
    }

    [Fact]
    public void NoCap_NeverTrips()
    {
        using var cts = new CancellationTokenSource();
        var collector = new JobApiCallCollector(_ => { }, maxCost: 0, capTrip: cts);
        for (var i = 0; i < 50; i++) collector.Record(Call(1m));
        cts.IsCancellationRequested.Should().BeFalse();
    }
}
