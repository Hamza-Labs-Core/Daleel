using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Models;

public class ProductModelImageTests
{
    [Theory]
    [InlineData("null")]        // extractor emitted the literal string "null"
    [InlineData("undefined")]
    [InlineData("N/A")]
    [InlineData("/images/x.jpg")] // relative path, not a usable remote image
    [InlineData("  ")]
    public void CandidateImages_ExcludesJunkImageUrls(string junk)
    {
        // Junk in the image field used to pass "not whitespace", become a candidate, get promoted as
        // "verified" (the vision screen skips non-http urls) and render <img src="null"> → 404.
        var model = new ProductModel { Name = "Dress", ImageUrl = junk };

        model.CandidateImages.Should().BeEmpty();
        model.DisplayImageUrl.Should().BeNull();
    }

    [Fact]
    public void CandidateImages_KeepsRealHttpUrls_DropsJunkFromGallery()
    {
        var model = new ProductModel
        {
            Name = "Dress",
            ImageUrl = "null",
            Images = new[] { "https://x/a.jpg", "null", "https://x/b.jpg" }
        };

        model.CandidateImages.Should().Equal("https://x/a.jpg", "https://x/b.jpg");
    }
}
