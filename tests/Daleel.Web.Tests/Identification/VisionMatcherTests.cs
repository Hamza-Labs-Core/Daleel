using System.Net;
using System.Text;
using Daleel.Web.Identification;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Identification;

/// <summary>
/// Covers the vision matcher's wire format (it sends two <c>image_url</c> parts to an OpenAI-compatible
/// endpoint) and its parsing of the model's JSON verdict, plus the graceful-degradation guards.
/// </summary>
public class VisionMatcherTests
{
    private const string StoreImg = "https://store.jo/photo.jpg";
    private const string BrandImg = "https://images.daleel.app/brands/1/abc.jpg";

    [Fact]
    public async Task CompareAsync_SendsBothImagesAndParsesVerdict()
    {
        var handler = new CapturingHandler(ChatResponse("""{"same_product": true, "confidence": 0.92, "model_name": "Galaxy S24"}"""));
        using var http = new HttpClient(handler);
        using var matcher = new VisionMatcher("key", "anthropic/claude-sonnet-4", NullLogger<VisionMatcher>.Instance, http);

        var result = await matcher.CompareAsync(StoreImg, BrandImg, "Galaxy S24");

        result.SameProduct.Should().BeTrue();
        result.Confidence.Should().Be(0.92);
        result.ModelName.Should().Be("Galaxy S24");

        handler.LastBody.Should().Contain("image_url");
        handler.LastBody.Should().Contain(StoreImg).And.Contain(BrandImg);
        handler.LastRequest!.Headers.Authorization!.Parameter.Should().Be("key");
    }

    [Fact]
    public async Task CompareAsync_ReturnsNoMatch_OnHttpError()
    {
        var handler = new CapturingHandler("nope", HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);
        using var matcher = new VisionMatcher("key", null, NullLogger<VisionMatcher>.Instance, http);

        (await matcher.CompareAsync(StoreImg, BrandImg, null)).Should().Be(VisionMatchResult.NoMatch);
    }

    [Theory]
    [InlineData("not-a-url", BrandImg)]
    [InlineData(StoreImg, "ftp://x/y.jpg")]
    public async Task CompareAsync_SkipsNonHttpUrls_WithoutCallingTheModel(string store, string brand)
    {
        var handler = new CapturingHandler(ChatResponse("""{"same_product": true, "confidence": 1}"""));
        using var http = new HttpClient(handler);
        using var matcher = new VisionMatcher("key", null, NullLogger<VisionMatcher>.Instance, http);

        (await matcher.CompareAsync(store, brand, null)).Should().Be(VisionMatchResult.NoMatch);
        handler.LastRequest.Should().BeNull("no HTTP call should be made for unusable image URLs");
    }

    [Fact]
    public void Parse_ClampsConfidenceAndNullsBlankModelName()
    {
        var r = VisionMatcher.Parse(ChatResponse("""{"same_product": false, "confidence": 5, "model_name": ""}"""));
        r.Confidence.Should().Be(1.0);
        r.ModelName.Should().BeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"choices": []}""")]
    [InlineData("garbage")]
    public void Parse_ReturnsNoMatch_ForUnusableResponses(string body)
    {
        VisionMatcher.Parse(body).Should().Be(VisionMatchResult.NoMatch);
    }

    [Fact]
    public void NullVisionMatcher_IsNotConfigured_AndNeverMatches()
    {
        var m = new NullVisionMatcher();
        m.IsConfigured.Should().BeFalse();
    }

    private static string ChatResponse(string content)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(content);
        return "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + escaped + "}}]}";
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public CapturingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}
