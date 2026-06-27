using Daleel.Web.Email;
using Daleel.Web.Resources;
using FluentAssertions;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Daleel.Web.Tests.Email;

/// <summary>
/// Structural guarantees for the search-result email HTML: the things that make it render correctly in
/// real clients (inline CSS + tables, no &lt;style&gt; block), carry the search data (query, counts,
/// product/store cards, CTA), localize via the shared localizer, and lay out RTL for Arabic.
/// </summary>
public class SearchResultEmailTemplateTests
{
    private static SearchResultEmailTemplate Build() =>
        new(new KeyEchoLocalizer());

    private static SearchResultEmailModel SampleModel(string language = "en") => new()
    {
        Query = "air conditioners",
        Language = language,
        ProductCount = 12,
        BrandCount = 4,
        StoreCount = 7,
        CtaUrl = "https://daleel.hamzalabs.dev/history",
        TopProducts = new[]
        {
            new EmailProduct("Gree Pular 24000 BTU", "320 JD – 450 JD", "https://img.example/gree.jpg"),
            new EmailProduct("Samsung WindFree", "599 JD", null),
            new EmailProduct("LG DualCool", null, "https://img.example/lg.jpg"),
        },
        TopStores = new[]
        {
            new EmailStore("Smart Buy", "Amman, Jordan"),
            new EmailStore("Leaders Center", null),
        }
    };

    [Fact]
    public void Render_Subject_ComesFromLocalizer()
    {
        var result = Build().Render(SampleModel());

        // The KeyEchoLocalizer returns the key, proving the subject is pulled from the localizer.
        result.Subject.Should().Be("Email.Search.Subject");
    }

    [Fact]
    public void Render_IncludesQueryCountsAndCta()
    {
        var html = Build().Render(SampleModel()).HtmlBody;

        html.Should().Contain("air conditioners");
        html.Should().Contain("12").And.Contain("4").And.Contain("7");
        html.Should().Contain("https://daleel.hamzalabs.dev/history");
    }

    [Fact]
    public void Render_IncludesTopProducts_WithNamesPricesAndImages()
    {
        var html = Build().Render(SampleModel()).HtmlBody;

        html.Should().Contain("Gree Pular 24000 BTU");
        html.Should().Contain("320 JD – 450 JD");
        html.Should().Contain("https://img.example/gree.jpg");
        html.Should().Contain("Samsung WindFree").And.Contain("599 JD");
        html.Should().Contain("LG DualCool");
    }

    [Fact]
    public void Render_IncludesTopStores_WithNamesAndLocation()
    {
        var html = Build().Render(SampleModel()).HtmlBody;

        html.Should().Contain("Smart Buy").And.Contain("Amman, Jordan");
        html.Should().Contain("Leaders Center");
    }

    [Fact]
    public void Render_IsEmailClientSafe_InlineCssAndTables_NoStyleBlock()
    {
        var html = Build().Render(SampleModel()).HtmlBody;

        html.Should().Contain("<table");        // table-based layout
        html.Should().Contain("style=");         // inline CSS
        html.Should().NotContain("<style");      // clients strip <style> blocks
    }

    [Fact]
    public void Render_EnglishIsLtr_ArabicIsRtl()
    {
        Build().Render(SampleModel("en")).HtmlBody.Should().Contain("dir=\"ltr\"");

        var ar = Build().Render(SampleModel("ar")).HtmlBody;
        ar.Should().Contain("dir=\"rtl\"");
    }

    [Fact]
    public void Render_EscapesHtmlInQuery()
    {
        var model = SampleModel() with { Query = "<script>alert(1)</script>" };

        var html = Build().Render(model).HtmlBody;

        html.Should().NotContain("<script>alert(1)</script>");
        html.Should().Contain("&lt;script&gt;");
    }

    /// <summary>A localizer that echoes the key back as the value — lets tests assert text came from it.</summary>
    private sealed class KeyEchoLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(name, arguments), resourceNotFound: false);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            Enumerable.Empty<LocalizedString>();
    }
}
