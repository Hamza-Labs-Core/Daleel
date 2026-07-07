using Daleel.Search.Http;
using Daleel.Web.Pipeline.Enrichment;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// The infra-vs-terminal split that decides Requeue (park, no attempt consumed) vs Retry (counts toward
/// Dead). A billing/infra outage must be classified infra so pending work survives it.
/// </summary>
public class EnrichmentErrorClassifierTests
{
    [Theory]
    [InlineData(402)] // OpenRouter out-of-credits — the exact production failure
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void ProviderException_with_infra_status_is_retriable_infra(int status)
    {
        var ex = new ProviderException($"openrouter: HTTP {status}.") { StatusCode = status };
        EnrichmentErrorClassifier.IsRetriableInfra(ex).Should().BeTrue();
    }

    [Fact]
    public void Transient_flagged_provider_exception_is_infra()
    {
        EnrichmentErrorClassifier.IsRetriableInfra(
            new ProviderException("x: request failed after retries.") { IsTransient = true }).Should().BeTrue();
    }

    [Fact]
    public void Transport_exceptions_are_infra()
    {
        EnrichmentErrorClassifier.IsRetriableInfra(new HttpRequestException("connection reset")).Should().BeTrue();
        EnrichmentErrorClassifier.IsRetriableInfra(new IOException("socket closed")).Should().BeTrue();
    }

    [Fact]
    public void A_402_that_only_survives_in_the_message_is_still_infra()
    {
        // The LLM path can surface the status as text; the message fallback must catch a billing failure.
        EnrichmentErrorClassifier.IsRetriableInfra(
            new InvalidOperationException("openrouter: HTTP 402 PaymentRequired. add credits")).Should().BeTrue();
    }

    [Theory]
    [InlineData(400)] // bad request — genuinely terminal, must NOT park forever
    [InlineData(404)]
    [InlineData(422)]
    public void ProviderException_with_client_error_status_is_terminal(int status)
    {
        var ex = new ProviderException($"x: HTTP {status}.") { StatusCode = status };
        EnrichmentErrorClassifier.IsRetriableInfra(ex).Should().BeFalse();
    }

    [Fact]
    public void An_ordinary_bug_is_terminal()
    {
        EnrichmentErrorClassifier.IsRetriableInfra(new ArgumentNullException("payload")).Should().BeFalse();
    }
}
