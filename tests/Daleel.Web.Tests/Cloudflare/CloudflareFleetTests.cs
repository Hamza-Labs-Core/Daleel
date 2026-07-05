using System.Net;
using System.Text;
using Daleel.Web.Cloudflare;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Cloudflare;

/// <summary>
/// The Workers-AI fleet client (classify / extract / filter hosts): per-endpoint configuration,
/// envelope parsing, and the best-effort degrade contract (a failing host yields empty signals,
/// never an exception into the caller).
/// </summary>
public class CloudflareFleetTests
{
    [Fact]
    public void FleetOptions_NullWhenNothingConfigured_PerEndpointOtherwise()
    {
        CloudflareFleetOptions.FromConfiguration(Config(new())).Should().BeNull(
            "an unconfigured fleet must leave the app exactly as before");

        var options = CloudflareFleetOptions.FromConfiguration(Config(new()
        {
            ["CF_FILTER_WORKER_URL"] = "https://filter.test",
            ["CF_FILTER_WORKER_TOKEN"] = "t",
            ["CF_CLASSIFY_WORKER_URL"] = "https://classify.test", // token missing ⇒ vault-era endpoint
            ["CF_EXTRACT_WORKER_URL"] = "not a url"               // malformed ⇒ endpoint null
        }));

        options.Should().NotBeNull();
        options!.Filter.Should().NotBeNull();
        options.Filter!.Token.Should().Be("t");
        // Token-authority deployments render the _TOKEN env vars empty on purpose — the vault
        // serves bearers per request, so the URL alone configures the endpoint.
        options.Classify.Should().NotBeNull("the vault supplies bearers at request time");
        options.Classify!.Token.Should().BeNull();
        options.Search.Should().BeNull();
        options.Extract.Should().BeNull("a malformed URL is not an endpoint");
    }

    [Fact]
    public async Task ClassifyText_ParsesVerdicts_AndSendsBearer()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK,
            """{ "ok": true, "result": { "verdicts": [ { "id": "a", "label": "refurbished", "confidence": 0.94, "reason": "explicit" } ] } }"""));
        var client = Client(handler, classify: true);

        var verdicts = await client.ClassifyTextAsync(
            new[] { ("a", "Refurbished — 90-day warranty") }, new[] { "new", "used", "refurbished" });

        verdicts.Should().ContainSingle(v => v.Id == "a" && v.Label == "refurbished" && v.Confidence == 0.94);
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("token");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/classify/text");
    }

    [Fact]
    public async Task FilterText_ParsesFindings_AndDegradesToEmptyOnFailure()
    {
        var ok = Client(new StubHandler(_ => (HttpStatusCode.OK,
            """{ "ok": true, "result": { "findings": [ { "id": "x", "category": "alcohol", "confidence": 0.9, "source": "llm" } ] } }""")),
            filter: true);
        (await ok.FilterTextAsync(new[] { ("x", "wine cellar", (string?)null) }))
            .Should().ContainSingle(f => f.Category == "alcohol" && f.Source == "llm");

        var down = Client(new StubHandler(_ => throw new HttpRequestException("down")), filter: true);
        (await down.FilterTextAsync(new[] { ("x", "text", (string?)null) }))
            .Should().BeEmpty("fleet signals are best-effort — a dead host means 'no finding', never a fault");

        var rejecting = Client(new StubHandler(_ => (HttpStatusCode.BadRequest,
            """{ "ok": false, "error": { "code": "bad_request", "message": "too many items", "retryable": false } }""")),
            filter: true);
        (await rejecting.FilterTextAsync(new[] { ("x", "text", (string?)null) })).Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractProducts_DeserializesIntoCatalogProducts()
    {
        var client = Client(new StubHandler(_ => (HttpStatusCode.OK,
            """{ "ok": true, "result": { "products": [ { "name": "Espresso X", "price": 139, "currency": "JOD", "sku": "EX1" } ], "productCount": 1 } }""")),
            extract: true);

        var products = await client.ExtractProductsAsync("<html>…</html>", "jordan");

        products.Should().ContainSingle(p => p.Name == "Espresso X" && p.Price == 139m && p.Sku == "EX1");
    }

    [Fact]
    public async Task UnconfiguredCapabilities_AreInertEmpty()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, "{}"));
        var client = Client(handler, classify: true); // ONLY classify configured

        (await client.FilterTextAsync(new[] { ("x", "t", (string?)null) })).Should().BeEmpty();
        (await client.ExtractProductsAsync("html")).Should().BeEmpty();
        client.HasFilter.Should().BeFalse();
        client.HasExtract.Should().BeFalse();
        client.HasClassify.Should().BeTrue();
        handler.Requests.Should().BeEmpty("unconfigured capabilities must make zero HTTP calls");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────

    private static CloudflareFleetClient Client(
        StubHandler handler, bool classify = false, bool extract = false, bool filter = false)
    {
        var endpoint = (string host) => new WorkerEndpoint(new Uri($"https://{host}.test"), "token");
        var options = new CloudflareFleetOptions
        {
            Classify = classify ? endpoint("classify") : null,
            Extract = extract ? endpoint("extract") : null,
            Filter = filter ? endpoint("filter") : null
        };
        return new CloudflareFleetClient(options, () => new HttpClient(handler), NullLogger<CloudflareFleetClient>.Instance);
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = _responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
