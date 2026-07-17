using Daleel.Web.Moderation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Moderation;

public class ProductImageScreenTests
{
    private static string Wrap(string content) =>
        "{\"choices\": [{\"message\": {\"content\": "
        + System.Text.Json.JsonSerializer.Serialize(content)
        + "}}]}";

    [Fact]
    public void Parse_ReturnsRejectIndices()
    {
        var reject = OpenRouterProductImageScreen.Parse(Wrap("""{"reject": [0, 2]}"""), batchCount: 3);
        reject.Should().BeEquivalentTo(new[] { 0, 2 });
    }

    [Fact]
    public void Parse_EmptyReject_MeansAllAreProductShots()
    {
        var reject = OpenRouterProductImageScreen.Parse(Wrap("""{"reject": []}"""), batchCount: 3);
        reject.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Parse_IgnoresOutOfRangeIndices()
    {
        var reject = OpenRouterProductImageScreen.Parse(Wrap("""{"reject": [1, 9, -1]}"""), batchCount: 3);
        reject.Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public void Parse_Malformed_ReturnsNull_ForFailOpen()
    {
        // A null parse means "reject nothing" upstream — a bad response must never hide good images.
        OpenRouterProductImageScreen.Parse("not json", 3).Should().BeNull();
        OpenRouterProductImageScreen.Parse(Wrap("no reject field here"), 3).Should().BeNull();
    }

    [Fact]
    public async Task RejectNonProductShots_FlagsTheLogo_KeepsProductAndLifestylePhotos()
    {
        // index 1 (a marketplace logo, no product) is rejected; the studio shot AND the in-room lifestyle
        // shot both stay — a styled photo that shows the product is fine.
        var handler = new StubHandler(_ => Wrap("""{"reject": [1]}"""));
        using var screen = Build(handler);

        var reject = await screen.RejectNonProductShotsAsync(new[]
        {
            "https://store.jo/product/ac-white.jpg",
            "https://opensooq.jo/opensooq_logo.png",
            "https://store.jo/product/ac-in-living-room.jpg",
        });

        reject.Should().BeEquivalentTo(new[] { "https://opensooq.jo/opensooq_logo.png" });
    }

    [Fact]
    public async Task RejectNonProductShots_OnHttpFailure_RejectsNothing_FailOpen()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("boom"));
        using var screen = Build(handler);

        var reject = await screen.RejectNonProductShotsAsync(new[] { "https://store.jo/product/ac.jpg" });

        reject.Should().BeEmpty("a screen outage must never hide a good image");
    }

    private static OpenRouterProductImageScreen Build(StubHandler handler) =>
        new("key", VisionModelResolver.Pinned("m"),
            NullLogger<OpenRouterProductImageScreen>.Instance,
            cache: null,
            http: new HttpClient(handler));

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), System.Text.Encoding.UTF8, "application/json")
            });
    }
}
