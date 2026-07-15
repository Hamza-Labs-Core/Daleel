using Daleel.Search.Http;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

// Context.dev depletion (USAGE_EXCEEDED) is an EXPECTED steady state, not a bug: the chain exists so
// Cloudflare Browser takes over. These tests pin the latch — once depletion is seen, further calls
// fail fast WITHOUT hitting the network, so the router drops to the fallback instantly instead of
// re-hammering a dead quota (3 retry round-trips) on every page.
public class ContextDevProviderDepletionTests
{
    // No real backoff waits during the retry loop.
    private static ContextDevProvider Build(StubHttpMessageHandler handler) =>
        new(apiKey: "k", httpClient: handler.Client(ContextDevProvider.DefaultBaseUrl),
            delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Scrape_latches_depletion_from_a_200_body_and_stops_calling_the_network()
    {
        var handler = new StubHttpMessageHandler("""{"success":false,"error":"USAGE_EXCEEDED"}""");
        var provider = Build(handler);

        var first = await provider.ScrapeAsync("https://store.example/a");
        first.Success.Should().BeFalse("a depletion payload carries no markdown");
        provider.IsDepleted.Should().BeTrue("the marker in the body must latch the provider");

        var callsAfterFirst = handler.Requests.Count;
        var second = await provider.ScrapeAsync("https://store.example/b");

        second.Success.Should().BeFalse();
        handler.Requests.Count.Should().Be(callsAfterFirst,
            "once depleted, further scrapes short-circuit to the fallback without any HTTP call");
    }

    [Fact]
    public async Task Extract_short_circuits_once_depleted_without_hitting_the_network()
    {
        var handler = new StubHttpMessageHandler("""{"success":false,"code":"USAGE_EXCEEDED"}""");
        var provider = Build(handler);

        await provider.ScrapeAsync("https://store.example/a"); // trips the latch
        provider.IsDepleted.Should().BeTrue();
        var callsBeforeExtract = handler.Requests.Count;

        var act = async () => await provider.ExtractAsync("https://store.example/p", new { });

        await act.Should().ThrowAsync<ProviderException>()
            .Where(e => e.Message.Contains("USAGE_EXCEEDED"),
                "the router catches this and falls back to Cloudflare Browser");
        handler.Requests.Count.Should().Be(callsBeforeExtract,
            "a depleted extract must not spend a network round-trip");
    }

    [Fact]
    public async Task Depletion_from_a_429_error_body_latches_after_the_retries_exhaust()
    {
        var handler = new StubHttpMessageHandler(
            """{"error":"USAGE_EXCEEDED","message":"quota spent"}""", System.Net.HttpStatusCode.TooManyRequests);
        var provider = Build(handler);

        var first = await provider.ScrapeAsync("https://store.example/a");
        first.Success.Should().BeFalse();
        provider.IsDepleted.Should().BeTrue("the marker survives in the post-retry error message");

        var afterFirst = handler.Requests.Count;
        afterFirst.Should().BeGreaterThan(1, "a 429 is transient, so the first call exhausts its retries");

        await provider.ScrapeAsync("https://store.example/b");
        handler.Requests.Count.Should().Be(afterFirst, "but every subsequent call is short-circuited");
    }
}
