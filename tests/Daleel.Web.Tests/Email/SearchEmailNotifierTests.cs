using Daleel.Agent;
using Daleel.Core.Models;
using Daleel.Web.Data;
using Daleel.Web.Email;
using Daleel.Web.Resources;
using Daleel.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Email;

/// <summary>
/// The notifier's decision logic: it sends only when email is configured, the user has an address, and
/// they're opted in — and it never throws (best-effort), so a delivery failure can't break the search.
/// </summary>
public class SearchEmailNotifierTests
{
    private static string ResultJson(string query = "air conditioners") =>
        ResultSerialization.Serialize(new AgentAnswer
        {
            Question = query,
            Products = new ProductSearchResult
            {
                Models = new[] { new ProductModel { Name = "Gree Pular" } },
                Stores = new[] { new StoreInfo { Name = "Smart Buy" } }
            }
        });

    private static SearchJob Job(string lang = "en") =>
        new() { UserId = "u1", Query = "air conditioners", Language = lang };

    private static SearchEmailNotifier Build(
        FakeEmail email, EmailRecipient? recipient) =>
        new(email,
            new FakeRecipients(recipient),
            new SearchResultEmailTemplate(new KeyEchoLocalizer()),
            new EmailNotificationOptions("https://daleel.hamzalabs.dev"),
            NullLogger<SearchEmailNotifier>.Instance);

    [Fact]
    public async Task Sends_WhenEnabled_OptedIn_WithAddress()
    {
        var email = new FakeEmail(enabled: true);
        var notifier = Build(email, new EmailRecipient("user@example.com", "User", WantsSearchEmails: true));

        await notifier.NotifySearchCompletedAsync(Job(), ResultJson());

        email.Sent.Should().HaveCount(1);
        email.Sent[0].To.Should().Be("user@example.com");
        email.Sent[0].Subject.Should().Be("Email.Search.Subject");
        email.Sent[0].HtmlBody.Should().Contain("air conditioners");
        email.Sent[0].HtmlBody.Should().Contain("https://daleel.hamzalabs.dev/history");
    }

    [Fact]
    public async Task Skips_WhenEmailServiceDisabled()
    {
        var email = new FakeEmail(enabled: false);
        var notifier = Build(email, new EmailRecipient("user@example.com", null, true));

        await notifier.NotifySearchCompletedAsync(Job(), ResultJson());

        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_WhenNoRecipient()
    {
        var email = new FakeEmail(enabled: true);
        var notifier = Build(email, recipient: null);

        await notifier.NotifySearchCompletedAsync(Job(), ResultJson());

        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_WhenUserOptedOut()
    {
        var email = new FakeEmail(enabled: true);
        var notifier = Build(email, new EmailRecipient("user@example.com", null, WantsSearchEmails: false));

        await notifier.NotifySearchCompletedAsync(Job(), ResultJson());

        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_WhenResultJsonIsUnusable()
    {
        var email = new FakeEmail(enabled: true);
        var notifier = Build(email, new EmailRecipient("user@example.com", null, true));

        await notifier.NotifySearchCompletedAsync(Job(), "not json");

        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task NeverThrows_WhenSendFails()
    {
        var email = new FakeEmail(enabled: true) { Throw = true };
        var notifier = Build(email, new EmailRecipient("user@example.com", null, true));

        var act = async () => await notifier.NotifySearchCompletedAsync(Job(), ResultJson());

        await act.Should().NotThrowAsync();
    }

    private sealed class FakeEmail : IEmailService
    {
        public FakeEmail(bool enabled) => IsEnabled = enabled;
        public bool IsEnabled { get; }
        public bool Throw { get; init; }
        public List<EmailMessage> Sent { get; } = new();

        public Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("boom");
            Sent.Add(message);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRecipients : IUserEmailPreferences
    {
        private readonly EmailRecipient? _recipient;
        public FakeRecipients(EmailRecipient? recipient) => _recipient = recipient;
        public Task<EmailRecipient?> GetRecipientAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult(_recipient);
    }

    private sealed class KeyEchoLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(name, arguments), resourceNotFound: false);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            Enumerable.Empty<LocalizedString>();
    }
}
