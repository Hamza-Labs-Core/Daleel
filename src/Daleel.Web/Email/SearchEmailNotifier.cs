using System.Globalization;
using Daleel.Agent;
using Daleel.Web.Data;
using Daleel.Web.Services;

namespace Daleel.Web.Email;

/// <summary>Where the email's "View Full Report" button points — the public base URL of the site.</summary>
public sealed record EmailNotificationOptions(string BaseUrl);

/// <summary>
/// Sends the "your search results are ready" email when a search completes. Wired into the search runner
/// and called best-effort: every path swallows its own failure so a delivery problem can never fail (or
/// slow) the search the user already received.
/// </summary>
public interface ISearchEmailNotifier
{
    /// <summary>
    /// Emails the user who owns <paramref name="job"/> a summary of <paramref name="resultJson"/>. A no-op
    /// when email is unconfigured, the user has no address, or they've opted out. Never throws.
    /// </summary>
    Task NotifySearchCompletedAsync(SearchJob job, string resultJson, CancellationToken ct = default);
}

/// <inheritdoc cref="ISearchEmailNotifier"/>
public sealed class SearchEmailNotifier : ISearchEmailNotifier
{
    private readonly IEmailService _email;
    private readonly IUserEmailPreferences _recipients;
    private readonly SearchResultEmailTemplate _template;
    private readonly EmailNotificationOptions _options;
    private readonly ILogger<SearchEmailNotifier> _logger;

    public SearchEmailNotifier(
        IEmailService email, IUserEmailPreferences recipients, SearchResultEmailTemplate template,
        EmailNotificationOptions options, ILogger<SearchEmailNotifier> logger)
    {
        _email = email;
        _recipients = recipients;
        _template = template;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Upper bound on the whole best-effort notification. Email runs on the search's completion path, and
    /// <see cref="HttpClient"/>'s default timeout is 100s — without this a slow/unresponsive provider would
    /// delay the user's result (and credit charge) by that long. A timeout here just logs and returns.
    /// </summary>
    private static readonly TimeSpan NotifyBudget = TimeSpan.FromSeconds(15);

    public async Task NotifySearchCompletedAsync(SearchJob job, string resultJson, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(NotifyBudget);
        var budgetCt = timeout.Token;
        try
        {
            // No provider configured → nothing to do (and don't bother hitting the DB for the user).
            if (!_email.IsEnabled || string.IsNullOrWhiteSpace(resultJson))
            {
                return;
            }

            var recipient = await _recipients.GetRecipientAsync(job.UserId, budgetCt).ConfigureAwait(false);
            if (recipient is null || !recipient.WantsSearchEmails)
            {
                return; // unknown user, no address, or opted out
            }

            // A non-product / informational / partial result has nothing to summarise — skip rather than
            // send an empty "0 products / 0 brands / 0 stores" email.
            if (ResultSerialization.Deserialize<AgentAnswer>(resultJson) is not { Products: not null } answer)
            {
                return;
            }

            var language = string.IsNullOrWhiteSpace(job.Language) ? "en" : job.Language;
            var ctaUrl = $"{_options.BaseUrl.TrimEnd('/')}/history";
            var model = SearchResultEmailModel.From(answer, language, ctaUrl);

            // Render under the user's culture so SharedResource resolves EN vs AR. The worker thread's
            // ambient culture isn't the user's, so set it for the render and restore it afterwards.
            var content = RenderForCulture(language, model);

            var message = new EmailMessage
            {
                To = recipient.Email,
                Subject = content.Subject,
                HtmlBody = content.HtmlBody,
                TextBody = content.TextBody
            };

            await _email.SendAsync(message, budgetCt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: emailing happens after the search already succeeded, so a failure here (bad
            // JSON, a culture lookup, the provider, or exceeding NotifyBudget) must never surface to the
            // caller.
            _logger.LogWarning(ex, "Search-result email failed for job {JobId}", job.Id);
        }
    }

    private EmailContent RenderForCulture(string language, SearchResultEmailModel model)
    {
        var prevCulture = CultureInfo.CurrentCulture;
        var prevUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = new CultureInfo(language);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return _template.Render(model);
        }
        catch (CultureNotFoundException)
        {
            // Unknown language code — fall back to whatever the localizer resolves by default.
            return _template.Render(model);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
            CultureInfo.CurrentUICulture = prevUiCulture;
        }
    }
}
