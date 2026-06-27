using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Daleel.Web.Email;

/// <summary>
/// Sends email through the Resend HTTP API (https://resend.com) with a plain <see cref="HttpClient"/>
/// POST to <c>/emails</c> — no SDK needed. Best-effort: a non-2xx response or a transport error is logged
/// and surfaced as <c>false</c>, never thrown, so a delivery failure can't break the caller.
/// </summary>
public sealed class ResendEmailService : IEmailService
{
    private const string SendUrl = "https://api.resend.com/emails";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _from;
    private readonly ILogger<ResendEmailService> _logger;

    public bool IsEnabled => true;

    public ResendEmailService(string apiKey, string from, HttpClient http, ILogger<ResendEmailService> logger)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _from = from ?? throw new ArgumentNullException(nameof(from));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
    }

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var payload = new SendRequest(
            From: _from,
            To: new[] { message.To },
            Subject: message.Subject,
            Html: message.HtmlBody,
            Text: message.TextBody);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SendUrl)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // Non-2xx: log the status (and a short body excerpt) for operators, but never throw.
            var detail = await SafeReadAsync(response, ct).ConfigureAwait(false);
            _logger.LogWarning("Resend rejected email to {To}: {Status} {Detail}",
                message.To, (int)response.StatusCode, detail);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // honour cooperative cancellation
        }
        catch (Exception ex)
        {
            // Best-effort: a transport error must never fail the caller (the search already succeeded).
            _logger.LogWarning(ex, "Failed to send email to {To} via Resend", message.To);
            return false;
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length > 300 ? text[..300] : text;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>The Resend send-email request body. Snake-case to match the API contract.</summary>
    private sealed record SendRequest(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string[] To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string Html,
        [property: JsonPropertyName("text")] string? Text);
}
