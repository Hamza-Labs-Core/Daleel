using Daleel.Core.Observability;
using Daleel.Web.Conversation;
using Daleel.Web.Data;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// Billing-correctness fixes from the branch review: a call that returned but did not DELIVER
/// (a failed-edge attempt that fell back to inline) must bill neither dollars nor credits, so a
/// worker outage never double-charges a delivered page.
/// </summary>
public class MeteringBillingTests
{
    private sealed record Box(bool Ok);

    private static (Box? Result, ApiCall Recorded) MeterOnce(bool ok, Func<Box, bool>? success)
    {
        ApiCall? captured = null;
        var observer = new CaptureObserver(c => captured = c);
        var estimator = new CostEstimator(); // default pricing — scrape-worker resolves to a non-zero rate

        ApiCallTimer.TimeAsync(
            observer, estimator, "scrape-worker/context.dev", "scrape/markdown", "url",
            () => Task.FromResult<Box?>(new Box(ok)),
            bytes: _ => 100,
            success: success is null ? null : b => b is not null && success(b)).GetAwaiter().GetResult();

        return (new Box(ok), captured!);
    }

    [Fact]
    public void Undelivered_call_records_error_at_zero_cost()
    {
        var (_, rec) = MeterOnce(ok: false, success: b => b.Ok);
        rec.Status.Should().Be(ApiCallStatus.Error, "a returned-but-undelivered edge page is not a success");
        rec.EstimatedCost.Should().Be(0m, "an undelivered call must not count toward the dollar cap");
        rec.ResponseBytes.Should().Be(0);
    }

    [Fact]
    public void Delivered_call_records_success_at_full_cost()
    {
        var (_, rec) = MeterOnce(ok: true, success: b => b.Ok);
        rec.Status.Should().Be(ApiCallStatus.Success);
        rec.EstimatedCost.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void No_predicate_preserves_the_old_success_semantics()
    {
        var (_, rec) = MeterOnce(ok: false, success: null);
        rec.Status.Should().Be(ApiCallStatus.Success, "without a predicate a non-throwing call bills as before");
        rec.EstimatedCost.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Credits_bill_delivered_calls_only()
    {
        var collector = new JobApiCallCollector(_ => { }, maxCost: 0, capTrip: null);
        // A failed edge attempt (Error) then a successful inline scrape (Success) — one delivered page.
        collector.Record(new ApiCall
        {
            Provider = "scrape-worker/context.dev", Endpoint = "scrape/markdown",
            Status = ApiCallStatus.Error, EstimatedCost = 0m
        });
        collector.Record(new ApiCall
        {
            Provider = "Context.dev", Endpoint = "scrape/markdown",
            Status = ApiCallStatus.Success, EstimatedCost = 0.01m
        });

        collector.TotalCredits.Should().Be(
            CreditCost.ForCall("Context.dev", "scrape/markdown", null, null, 0.01m),
            "only the delivered inline scrape is billed — the failed edge attempt is free");
    }

    private sealed class CaptureObserver : IApiCallObserver
    {
        private readonly Action<ApiCall> _capture;
        public CaptureObserver(Action<ApiCall> capture) => _capture = capture;
        public void Record(ApiCall call) => _capture(call);
    }
}
