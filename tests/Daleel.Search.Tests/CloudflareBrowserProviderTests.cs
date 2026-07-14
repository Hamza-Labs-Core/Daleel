using System.Net;
using System.Text.Json;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

// Cloudflare's /browser-rendering/json endpoint validates response_format strictly: a "json_schema"
// type with a null/empty json_schema body is rejected with HTTP 422 on every call (32 such failures in
// one QA search). These tests pin the request-building contract: send json_schema only when we hold a
// real schema, and drop to freeform "json" otherwise so the render still runs.
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
    }

    [Fact]
    public async Task Extract_with_an_empty_schema_falls_back_to_plain_json_and_omits_json_schema()
    {
        var (handler, body) = CapturingHandler("""{"success":true,"result":{}}""");

        await Build(handler).ExtractAsync("https://store.example/x", new { });

        using var doc = JsonDocument.Parse(body()!);
        var rf = doc.RootElement.GetProperty("response_format");
        rf.GetProperty("type").GetString().Should()
            .Be("json", "an empty schema must never be sent as an empty json_schema body (CF answers 422)");
        rf.TryGetProperty("json_schema", out _).Should().BeFalse();
    }
}
