using System.Net;
using System.Text;

namespace Daleel.Search.Tests;

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that returns canned responses. Lets provider
/// tests exercise real request-building and response-parsing without any network. It can
/// match by a predicate on the request and records every request it saw.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _responder;

    public List<HttpRequestMessage> Requests { get; } = new();

    public StubHttpMessageHandler(string jsonBody, HttpStatusCode status = HttpStatusCode.OK)
        => _responder = _ => (status, jsonBody);

    public StubHttpMessageHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
        => _responder = responder;

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

    /// <summary>Builds an <see cref="HttpClient"/> wired to this handler with a base address.</summary>
    public HttpClient Client(string baseUrl) =>
        new(this) { BaseAddress = new Uri(baseUrl) };
}
