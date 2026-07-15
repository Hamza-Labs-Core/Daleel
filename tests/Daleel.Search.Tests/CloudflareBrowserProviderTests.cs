using System.Net;
using System.Text.Json;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

// Cloudflare's /browser-rendering/json endpoint validates response_format strictly. The ONLY valid
// type is "json_schema", and its body is a BARE JSON Schema object — not an OpenAI-style
// { name, schema } envelope, which CF rejects with 422. When we have no schema, response_format must
// be omitted entirely (prompt-only, the documented schema-less mode) — the old "json" type was itself
// invalid and 422'd every empty-schema call. These tests pin that request-building contract.
public class CloudflareBrowserProviderTests
{
    private static readonly object ListingSchema = new
    {
        type = "object",
        properties = new { products = new { type = "array" } }
    };

    private static CloudflareBrowserProvider Build(StubHttpMessageHandler handler) =>
        new(accountId: "acct", apiToken: "tok",
            httpClient: handler.Client(CloudflareBrowserProvider.DefaultBaseUrl));

    private static (StubHttpMessageHandler Handler, Func<string?> Body) CapturingHandler(string responseJson)
    {
        string? captured = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            captured = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return (HttpStatusCode.OK, responseJson);
        });
        return (handler, () => captured);
    }

    /// <summary>A handler that 422s any schema-constrained request (one carrying <c>response_format</c>)
    /// and returns <paramref name="schemaLessResponse"/> for the schema-less retry — recording every
    /// request body so a test can assert on the retry shape. Kept out of the <c>[Fact]</c> body so the
    /// synchronous body read doesn't trip xUnit1031 under the CI <c>-warnaserror</c> build.</summary>
    private static (StubHttpMessageHandler Handler, List<string> Requests) SchemaRejectingHandler(string schemaLessResponse)
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            var payload = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add(payload);
            return payload.Contains("response_format")
                ? ((HttpStatusCode)422, """{"success":false,"errors":[{"message":"invalid response_format"}]}""")
                : (HttpStatusCode.OK, schemaLessResponse);
        });
        return (handler, requests);
    }

    [Fact]
    public async Task Extract_with_a_real_schema_sends_json_schema_with_the_schema_body()
    {
        var (handler, body) = CapturingHandler("""{"success":true,"result":{"products":[]}}""");

        await Build(handler).ExtractAsync("https://store.example/x", ListingSchema);

        using var doc = JsonDocument.Parse(body()!);
        var rf = doc.RootElement.GetProperty("response_format");
        rf.GetProperty("type").GetString().Should().Be("json_schema");
        rf.TryGetProperty("json_schema", out var js).Should().BeTrue();
        js.GetProperty("type").GetString().Should().Be("object");
        js.GetProperty("properties").TryGetProperty("products", out _).Should()
            .BeTrue("the real schema body must ride along, never be dropped");

        // The schema must be sent BARE. CF's Browser Rendering docs never wrap it in an
        // OpenAI-style { name, schema } envelope — doing so is exactly what 422s the request.
        js.TryGetProperty("name", out _).Should()
            .BeFalse("json_schema is a bare schema object, not an OpenAI { name, schema } envelope");
        js.TryGetProperty("schema", out _).Should()
            .BeFalse("the schema fields sit directly under json_schema, never behind a nested 'schema' key");
    }

    [Fact]
    public async Task Extract_with_an_empty_schema_omits_response_format_and_sends_a_prompt()
    {
        var (handler, body) = CapturingHandler("""{"success":true,"result":{}}""");

        await Build(handler).ExtractAsync("https://store.example/x", new { });

        using var doc = JsonDocument.Parse(body()!);
        doc.RootElement.TryGetProperty("response_format", out _).Should()
            .BeFalse("with no schema, response_format must be omitted — 'json' is not a valid CF type (422)");
        doc.RootElement.GetProperty("prompt").GetString().Should()
            .NotBeNullOrWhiteSpace("schema-less extraction is prompt-guided, the documented fallback");
    }

    // The core self-heal: when CF 422s the schema-constrained request, the provider must NOT bubble a
    // hard failure to the router (whose only fallback is a depleted Context.dev). It re-issues the SAME
    // URL schema-less — drop response_format, pass a prompt — and parses whatever freeform JSON returns.
    [Fact]
    public async Task Extract_retries_schema_less_when_the_schema_request_422s()
    {
        var (handler, requests) = SchemaRejectingHandler(
            """{"success":true,"result":{"products":[{"name":"Kettle"}]}}""");

        var result = await Build(handler).ExtractAsync("https://store.example/x", ListingSchema);

        requests.Should().HaveCount(2, "one schema attempt that 422s, then one schema-less retry");
        requests[0].Should().Contain("response_format");
        using var retry = JsonDocument.Parse(requests[1]);
        retry.RootElement.TryGetProperty("response_format", out _).Should()
            .BeFalse("the retry must drop response_format entirely, not reshape it");
        retry.RootElement.GetProperty("prompt").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("products").GetArrayLength().Should()
            .Be(1, "the schema-less retry recovered the listing CF rejected under a schema");
    }

    // ── Navigation timeout ─────────────────────────────────────────────────────
    // Cloudflare's default navigation timeout is 30s; heavy Jordanian storefronts regularly blow it
    // (QA: code 6002 "Navigation timeout of 30000 ms exceeded" killed the drain's verifypage fetches,
    // so crawled cards kept "See price on store site" with no image). Every render request must carry
    // an explicit, longer gotoOptions.timeout. The PIPELINE stays fast regardless — its own 30s
    // PageReadTimeout CTS bounds those reads; only deadline-free callers (the enrichment drain) get
    // to use the full window.

    [Fact]
    public async Task Scrape_sends_a_longer_navigation_timeout()
    {
        var (handler, body) = CapturingHandler("""{"success":true,"result":"# page"}""");

        await Build(handler).ScrapeAsync("https://slow-store.example/p/1");

        using var doc = JsonDocument.Parse(body()!);
        doc.RootElement.GetProperty("gotoOptions").GetProperty("timeout").GetInt32()
            .Should().Be(CloudflareBrowserProvider.NavigationTimeoutMs)
            .And.BeGreaterThan(30_000, "CF's 30s default is exactly what times out on heavy storefronts");
    }

    [Fact]
    public async Task Extract_sends_the_same_navigation_timeout()
    {
        var (handler, body) = CapturingHandler("""{"success":true,"result":{"products":[]}}""");

        await Build(handler).ExtractAsync("https://store.example/x", ListingSchema);

        using var doc = JsonDocument.Parse(body()!);
        doc.RootElement.GetProperty("gotoOptions").GetProperty("timeout").GetInt32()
            .Should().Be(CloudflareBrowserProvider.NavigationTimeoutMs);
    }
}
