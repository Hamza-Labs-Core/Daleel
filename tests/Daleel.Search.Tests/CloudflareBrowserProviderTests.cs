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
}
