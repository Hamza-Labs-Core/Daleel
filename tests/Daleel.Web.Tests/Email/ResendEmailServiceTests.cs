using System.Net;
using System.Text.Json;
using Daleel.Web.Email;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Email;

/// <summary>
/// Verifies the Resend HTTP integration posts the documented request shape (Bearer auth, JSON body to
/// the /emails endpoint) and degrades to <c>false</c> — never throws — on a provider/transport failure.
/// </summary>
public class ResendEmailServiceTests
{
    private static readonly EmailMessage Sample = new()
    {
        To = "user@example.com",
        Subject = "Your results",
        HtmlBody = "<p>hello</p>",
        TextBody = "hello"
    };

    [Fact]
    public async Task SendAsync_PostsBearerAuthedJson_ToResendEndpoint()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHandler(async (req, ct) =>
        {
            captured = req;
            body = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"abc-123\"}")
            };
        });
        var service = new ResendEmailService("re_test_key", "noreply@daleel.hamzalabs.dev",
            new HttpClient(handler), NullLogger<ResendEmailService>.Instance);

        var ok = await service.SendAsync(Sample);

        ok.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.ToString().Should().Be("https://api.resend.com/emails");
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization!.Parameter.Should().Be("re_test_key");

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        root.GetProperty("from").GetString().Should().Be("noreply@daleel.hamzalabs.dev");
        root.GetProperty("to")[0].GetString().Should().Be("user@example.com");
        root.GetProperty("subject").GetString().Should().Be("Your results");
        root.GetProperty("html").GetString().Should().Be("<p>hello</p>");
        root.GetProperty("text").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task SendAsync_ReturnsFalse_OnProviderError()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("{\"error\":\"invalid\"}")
            }));
        var service = new ResendEmailService("re_test_key", "noreply@daleel.hamzalabs.dev",
            new HttpClient(handler), NullLogger<ResendEmailService>.Instance);

        var ok = await service.SendAsync(Sample);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_ReturnsFalse_OnTransportException()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("dns down"));
        var service = new ResendEmailService("re_test_key", "noreply@daleel.hamzalabs.dev",
            new HttpClient(handler), NullLogger<ResendEmailService>.Instance);

        var ok = await service.SendAsync(Sample);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task NullEmailService_IsDisabled_AndDropsMessages()
    {
        IEmailService service = new NullEmailService();

        service.IsEnabled.Should().BeFalse();
        (await service.SendAsync(Sample)).Should().BeFalse();
    }

    /// <summary>Routes each request to an inline handler so tests can assert on what was sent.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handle;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) =>
            _handle = handle;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            _handle(request, ct);
    }
}
