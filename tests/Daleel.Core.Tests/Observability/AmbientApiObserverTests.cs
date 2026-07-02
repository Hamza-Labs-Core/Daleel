using Daleel.Core.Observability;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Observability;

/// <summary>
/// The ambient observer is what routes paid calls made by DI-resolved components (vision matcher,
/// catalogue crawls) into the per-job collector. These pin its flow semantics: it must reach code
/// running under awaits and parallel fan-outs of the SAME job, restore on dispose, and never bleed
/// between two concurrent jobs' flows.
/// </summary>
public class AmbientApiObserverTests
{
    private sealed class CountingObserver : IApiCallObserver
    {
        public List<ApiCall> Calls { get; } = new();
        public void Record(ApiCall call) { lock (Calls) Calls.Add(call); }
    }

    [Fact]
    public async Task Begin_FlowsAcrossAwaits_AndRestoresOnDispose()
    {
        AmbientApiObserver.Observer.Should().BeNull("no scope is active");

        var observer = new CountingObserver();
        using (AmbientApiObserver.Begin(observer, new CostEstimator()))
        {
            await Task.Yield(); // cross an await boundary
            AmbientApiObserver.Observer.Should().BeSameAs(observer);
            AmbientApiObserver.Estimator.Should().NotBeNull();
        }

        AmbientApiObserver.Observer.Should().BeNull("dispose restores the previous (empty) ambient");
    }

    [Fact]
    public async Task Begin_FlowsIntoParallelChildren_OfTheSameJob()
    {
        var observer = new CountingObserver();
        using var _ = AmbientApiObserver.Begin(observer, new CostEstimator());

        // Task.WhenAll fan-out (how the enrichment crawls run): every child sees the job's observer.
        var seen = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await Task.Delay(1);
            return AmbientApiObserver.Observer;
        }));

        seen.Should().AllSatisfy(o => o.Should().BeSameAs(observer));
    }

    [Fact]
    public async Task ConcurrentJobs_NeverSeeEachOthersObserver()
    {
        var a = new CountingObserver();
        var b = new CountingObserver();

        // Two "jobs" running concurrently on separate async flows — the exact production shape
        // (two search workers). Each must only ever observe its own collector.
        var jobA = Task.Run(async () =>
        {
            using var _ = AmbientApiObserver.Begin(a, new CostEstimator());
            await Task.Delay(20);
            return AmbientApiObserver.Observer;
        });
        var jobB = Task.Run(async () =>
        {
            using var _ = AmbientApiObserver.Begin(b, new CostEstimator());
            await Task.Delay(20);
            return AmbientApiObserver.Observer;
        });

        (await jobA).Should().BeSameAs(a);
        (await jobB).Should().BeSameAs(b);
    }

    [Fact]
    public async Task ApiCallTimer_RecordsThroughTheAmbientObserver()
    {
        var observer = new CountingObserver();
        using var _ = AmbientApiObserver.Begin(observer, new CostEstimator());

        // The exact call shape the vision/catalogue sites use.
        var result = await ApiCallTimer.TimeAsync(
            AmbientApiObserver.Observer,
            AmbientApiObserver.Estimator ?? new CostEstimator(),
            "Context.dev", "catalog/extract", "store.example",
            () => Task.FromResult(42));

        result.Should().Be(42);
        observer.Calls.Should().ContainSingle();
        observer.Calls[0].Provider.Should().Be("Context.dev");
        observer.Calls[0].Endpoint.Should().Be("catalog/extract");
        observer.Calls[0].EstimatedCost.Should().BeGreaterThan(0, "catalogue crawls are paid calls");
    }
}
