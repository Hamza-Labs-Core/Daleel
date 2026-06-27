using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Services;

/// <summary>
/// The spec sanitizer is the chokepoint that keeps raw scraped page content out of the product detail
/// UI. Enrichment stows the raw scrape under a "details" key (and long blobs can leak in elsewhere); these
/// tests pin that such content is dropped while genuine short key/value specs survive.
/// </summary>
public class ProductSpecsTests
{
    [Fact]
    public void ForDisplay_DropsRawDetailsBlob_KeepsStructuredSpecs()
    {
        var rawBlob = "# Home\nMenu\nTVs\nPhones\n[Buy now](https://store.jo/x)\n" + new string('x', 4000);
        var specs = new Dictionary<string, string>
        {
            ["details"] = rawBlob,            // raw scraped page — must be dropped
            ["screen_size"] = "55 inches",    // clean spec — kept
            ["resolution"] = "4K UHD",        // clean spec — kept
        };

        var shown = ProductSpecs.ForDisplay(specs);

        shown.Select(kv => kv.Key).Should().BeEquivalentTo("screen_size", "resolution");
        shown.Should().NotContain(kv => kv.Key == "details");
    }

    [Theory]
    [InlineData("details", "anything")]                       // raw carrier key
    [InlineData("content", "anything")]
    [InlineData("screen", "line one\nline two")]              // multi-line → raw
    [InlineData("link", "see https://store.jo/x")]            // contains a URL
    [InlineData("md", "[click](http://x)")]                   // markdown link
    [InlineData("html", "<div>nav</div>")]                    // stray HTML
    [InlineData("empty", "")]
    public void IsCleanSpec_RejectsRawAndBlobbyValues(string key, string value)
    {
        ProductSpecs.IsCleanSpec(key, value).Should().BeFalse();
    }

    [Fact]
    public void IsCleanSpec_RejectsOverlongBlobValues()
    {
        ProductSpecs.IsCleanSpec("overview", new string('x', 250)).Should().BeFalse();
    }

    [Theory]
    [InlineData("screen_size", "55 inches")]
    [InlineData("brand", "Samsung")]
    [InlineData("weight", "12.5 kg")]
    public void IsCleanSpec_AcceptsCleanStructuredSpecs(string key, string value)
    {
        ProductSpecs.IsCleanSpec(key, value).Should().BeTrue();
    }
}
