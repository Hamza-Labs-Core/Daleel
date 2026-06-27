namespace Daleel.Web.Email;

/// <summary>
/// A single transactional email to send. Kept deliberately small: a recipient, a subject, and an
/// HTML body (with an optional plain-text alternative for clients that strip HTML).
/// </summary>
public sealed record EmailMessage
{
    public required string To { get; init; }
    public required string Subject { get; init; }
    public required string HtmlBody { get; init; }

    /// <summary>Optional plain-text fallback. Good email clients prefer the HTML part when both are sent.</summary>
    public string? TextBody { get; init; }
}

/// <summary>
/// Sends transactional email. Implemented over the Resend HTTP API when an API key is configured, or a
/// no-op (<see cref="NullEmailService"/>) when none is set — so the app runs out of the box and gains
/// outbound email the moment a key is provided. The whole feature is best-effort: a delivery failure
/// must never fail the search that triggered it.
/// </summary>
public interface IEmailService
{
    /// <summary>False for the no-op service; callers can skip building a message when email is off.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Sends <paramref name="message"/>. Returns true when the provider accepted it, false on any
    /// failure (never throws for a transport/provider error — only honours cancellation).
    /// </summary>
    Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>The service used when no Resend API key is configured: silently drops every message.</summary>
public sealed class NullEmailService : IEmailService
{
    public bool IsEnabled => false;

    public Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default) =>
        Task.FromResult(false);
}
