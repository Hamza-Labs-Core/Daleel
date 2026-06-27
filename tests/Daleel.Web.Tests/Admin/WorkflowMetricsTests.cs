using Daleel.Agent;
using Daleel.Core.Models;
using Daleel.Web.Admin;
using Daleel.Web.Data;
using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Admin;

/// <summary>
/// The pure helpers behind the admin Workflows page: parsing a result count out of a job's stored JSON
/// and computing a run's elapsed duration (including the still-running case).
/// </summary>
public sealed class WorkflowMetricsTests
{
    [Fact]
    public void ResultCount_CountsProductModels_FromAProductAnswer()
    {
        var answer = new AgentAnswer
        {
            Question = "best ACs",
            Products = new ProductSearchResult
            {
                Models = new List<ProductModel>
                {
                    new() { Name = "LG DualCool" },
                    new() { Name = "Samsung WindFree" },
                }
            }
        };

        WorkflowMetrics.ResultCount(ResultSerialization.Serialize(answer)).Should().Be(2);
    }

    [Fact]
    public void ResultCount_IsNull_ForAFreeFormAnswerWithNoProducts()
    {
        var answer = new AgentAnswer { Question = "why is the sky blue", Summary = "Rayleigh scattering." };
        WorkflowMetrics.ResultCount(ResultSerialization.Serialize(answer)).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    public void ResultCount_IsNull_ForMissingOrUnparseableJson(string? json)
    {
        WorkflowMetrics.ResultCount(json).Should().BeNull();
    }

    [Fact]
    public void Duration_UsesStartToComplete_WhenFinished()
    {
        var started = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var job = new SearchJob { StartedAt = started, CompletedAt = started.AddSeconds(42) };

        WorkflowMetrics.Duration(job, DateTimeOffset.UtcNow).Should().Be(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public void Duration_MeasuresAgainstNow_WhileStillRunning()
    {
        var started = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var now = started.AddSeconds(10);
        var job = new SearchJob { StartedAt = started, CompletedAt = null };

        WorkflowMetrics.Duration(job, now).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Duration_IsNull_WhenNeverStarted()
    {
        WorkflowMetrics.Duration(new SearchJob { StartedAt = null }, DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Duration_FloorsNegativeSkewAtZero()
    {
        var started = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var job = new SearchJob { StartedAt = started, CompletedAt = null };

        // "now" earlier than StartedAt (writer clock skew) must not produce a negative duration.
        WorkflowMetrics.Duration(job, started.AddSeconds(-5)).Should().Be(TimeSpan.Zero);
    }
}
