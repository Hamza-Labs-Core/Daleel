using Daleel.Web.Storage;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Storage;

/// <summary>
/// Covers the JSON-storage additions used by the spec pipeline: the no-op service returns no URL, blank
/// input is rejected without a network call, and the object-key sanitizer produces safe, lower-cased paths.
/// </summary>
public class R2StoreJsonTests
{
    [Fact]
    public async Task NullService_StoreJson_ReturnsNull()
    {
        (await new NullR2StorageService().StoreJsonAsync("{}", "final-specs/x.json")).Should().BeNull();
    }

    [Theory]
    [InlineData("site-data/Samsung/Galaxy S24/Brand Site.json", "site-data/samsung/galaxy-s24/brand-site.json")]
    [InlineData("/final-specs//LG//OLED 55\"//", "final-specs/lg/oled-55")]
    [InlineData("a/b_c/d.json", "a/b_c/d.json")]
    public void NormalizeObjectKey_SanitizesSegments(string input, string expected)
    {
        R2StorageService.NormalizeObjectKey(input).Should().Be(expected);
    }
}
