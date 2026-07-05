using System.Net;
using Daleel.Agent;
using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Pipeline.Enrichment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

public class ReachabilityProbeTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode? Status, Exception? Throw)> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode?, Exception?)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var (status, ex) = _respond(request);
            if (ex is not null)
            {
                throw ex;
            }
            return Task.FromResult(new HttpResponseMessage(status!.Value));
        }
    }

    private static ReachabilityProbe Probe(StubHandler handler) => new(new HttpClient(handler));

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Redirect, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]         // bot-defense — the shopper gets through
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Gone, false)]
    [InlineData(HttpStatusCode.UnavailableForLegalReasons, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task Status_classification(HttpStatusCode status, bool expected)
    {
        var probe = Probe(new StubHandler(_ => (status, null)));
        (await probe.IsReachableAsync($"https://{status}.example.com/x")).Should().Be(expected);
    }

    [Fact]
    public async Task Dns_or_connection_failure_is_unreachable()
    {
        var probe = Probe(new StubHandler(_ => (null, new HttpRequestException("No such host is known"))));
        (await probe.IsReachableAsync("https://ashrafi-mills.com/catalog")).Should().BeFalse(
            "NXDOMAIN hits the user's browser exactly the same way");
    }

    [Fact]
    public async Task Head_rejection_falls_back_to_get()
    {
        var handler = new StubHandler(r =>
            (r.Method == HttpMethod.Head ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.OK, null));
        (await Probe(handler).IsReachableAsync("https://headless.example.com/")).Should().BeTrue();
        handler.Requests.Select(r => r.Method).Should().Equal(HttpMethod.Head, HttpMethod.Get);
    }

    [Fact]
    public async Task Verdicts_are_cached_per_host()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, null));
        var probe = Probe(handler);
        await probe.IsReachableAsync("https://store.example.com/a");
        await probe.IsReachableAsync("https://store.example.com/b");
        handler.Requests.Should().HaveCount(1, "one probe answers for every offer on that host");
    }

    [Fact]
    public async Task Garbage_urls_are_unreachable_without_a_request()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, null));
        (await Probe(handler).IsReachableAsync("not a url")).Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }
}

public class ReachabilityHandlerTests
{
    private sealed class FakeProbe : IReachabilityProbe
    {
        public HashSet<string> Dead { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task<bool> IsReachableAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(!Dead.Contains(url));
    }

    private sealed class FixedResultStore : IEnrichedResultStore
    {
        public AgentAnswer? Answer { get; set; }
        public Task<AgentAnswer?> LoadAsync(int jobId, CancellationToken ct = default) => Task.FromResult(Answer);

        public Task<bool> PatchAsync(
            EnrichmentWorkItem item, Func<AgentAnswer, AgentAnswer?> mutate, CancellationToken ct = default)
        {
            if (Answer is null || mutate(Answer) is not { } patched)
            {
                return Task.FromResult(false);
            }
            Answer = patched;
            return Task.FromResult(true);
        }
    }

    private sealed class NoQueue : IEnrichmentWorkQueue
    {
        public Task EnqueueAsync(IReadOnlyList<EnrichmentWorkItem> items, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<EnrichmentWorkItem>> ClaimAsync(int max, TimeSpan lease, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EnrichmentWorkItem>>(Array.Empty<EnrichmentWorkItem>());
        public Task CompleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RetryAsync(long id, string reason, TimeSpan? delay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(long id, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> OpenCountAsync(int searchJobId, CancellationToken ct = default) => Task.FromResult(0);
    }

    [Fact]
    public async Task Dead_offers_are_pruned_but_the_model_card_survives()
    {
        var probe = new FakeProbe();
        probe.Dead.Add("https://dead-store.com/p/1");

        var store = new FixedResultStore
        {
            Answer = new AgentAnswer
            {
                Products = new ProductSearchResult
                {
                    Query = "q", Geo = "Jordan",
                    Models = new List<ProductModel>
                    {
                        new()
                        {
                            Name = "Maker A",
                            Offers = new List<PriceOffer>
                            {
                                new() { Source = "Dead Store", Url = "https://dead-store.com/p/1" },
                                new() { Source = "Live Store", Url = "https://live-store.jo/p/1" }
                            }
                        },
                        new()
                        {
                            Name = "Maker B",
                            Offers = new List<PriceOffer>
                            {
                                new() { Source = "Dead Store", Url = "https://dead-store.com/p/1" }
                            }
                        }
                    }
                }
            }
        };

        var ctx = new EnrichmentUnitContext
        {
            Services = new ServiceCollection().BuildServiceProvider(),
            Job = new SearchJob { Id = 7, UserId = "u1", Query = "q" },
            Agent = () => null!,
            Results = store,
            Queue = new NoQueue()
        };
        var item = new EnrichmentWorkItem
        {
            SearchJobId = 7, UserId = "u1", HistoryEntryId = 1, ResultType = "products",
            Kind = EnrichmentUnit.Reachability, Payload = "{}"
        };

        var outcome = await new ReachabilityHandler(probe, NullLogger<ReachabilityHandler>.Instance)
            .ExecuteAsync(item, ctx, default);

        outcome.Should().BeOfType<UnitOutcome.Done>();
        var models = store.Answer!.Products!.Models;
        models.Should().HaveCount(2, "losing every offer degrades a card to informational, never deletes it");
        models[0].Offers.Should().ContainSingle().Which.Url.Should().Be("https://live-store.jo/p/1");
        models[1].Offers.Should().BeEmpty();
    }
}
