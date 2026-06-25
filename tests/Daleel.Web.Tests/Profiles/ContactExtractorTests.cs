using Daleel.Web.Profiles;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Profiles;

/// <summary>
/// The conservative contact scraper that backstops store enrichment when neither the LLM nor
/// Google Places surfaced a phone/e-mail. It must catch real contacts in scraped markdown without
/// attaching junk (asset filenames, random digit runs) to a store profile, and the loose name match
/// must accept Places hits that differ only by decoration while rejecting unrelated places.
/// </summary>
public class ContactExtractorTests
{
    [Theory]
    [InlineData("Reach us at info@smartbuy.jo today", "info@smartbuy.jo")]
    [InlineData("Email: sales.team@store-amman.com.jo", "sales.team@store-amman.com.jo")]
    [InlineData("no contact here", null)]
    [InlineData("placeholder foo@example.com only", null)]
    public void FirstEmail_FindsRealAddressesOnly(string text, string? expected)
    {
        ContactExtractor.FirstEmail(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("Call +962 6 123 4567 for details", "+962 6 123 4567")]
    [InlineData("Tel 0791234567", "0791234567")]
    [InlineData("order #12345 ships soon", null)] // too short to be a phone
    [InlineData("plain prose with no number", null)]
    public void FirstPhone_FindsPlausiblePhonesOnly(string text, string? expected)
    {
        ContactExtractor.FirstPhone(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("Smart Buy", "Smart Buy Electronics", true)]
    [InlineData("Smart Buy", "smartbuy", true)]
    [InlineData("Smart Buy", "Carrefour", false)]
    [InlineData("", "anything", false)]
    public void NameMatches_IsLooseButNotBlind(string a, string b, bool expected)
    {
        ContextDevProfileResearcher.NameMatches(a, b).Should().Be(expected);
    }
}
