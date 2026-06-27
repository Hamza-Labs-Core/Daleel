using Daleel.Core.Models;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Models;

public class StableIdTests
{
    [Fact]
    public void ForProduct_IsDeterministic_AcrossCalls()
    {
        StableId.ForProduct("Samsung", "Galaxy S24")
            .Should().Be(StableId.ForProduct("Samsung", "Galaxy S24"));
    }

    [Fact]
    public void ForProduct_NormalizesCaseAndWhitespace()
    {
        StableId.ForProduct("samsung", "galaxy s24")
            .Should().Be(StableId.ForProduct("  Samsung ", " Galaxy S24  "));
    }

    [Fact]
    public void ForProduct_DistinguishesDifferentModels()
    {
        StableId.ForProduct("Samsung", "Galaxy S24")
            .Should().NotBe(StableId.ForProduct("Samsung", "Galaxy S23"));
    }

    [Fact]
    public void ForProduct_FallsBackToName_WhenBrandAndModelMissing()
    {
        StableId.ForProduct(null, null, "Some Loose Listing")
            .Should().Be(StableId.ForProduct("", "", "some loose listing"));
    }

    [Fact]
    public void Ids_AreUrlSafe_AndPrefixedByKind()
    {
        // Arabic / spaces / slashes in the source must not leak into the id.
        var product = StableId.ForProduct("سامسونج", "جالاكسي / S24");
        var brand = StableId.ForBrand("LG Electronics");
        var store = StableId.ForStore("Smart Buy");

        product.Should().StartWith("p_").And.MatchRegex("^p_[0-9a-f]{16}$");
        brand.Should().StartWith("b_").And.MatchRegex("^b_[0-9a-f]{16}$");
        store.Should().StartWith("s_").And.MatchRegex("^s_[0-9a-f]{16}$");
    }

    [Fact]
    public void DifferentKinds_WithSameName_ProduceDifferentIds()
    {
        // A brand and a store that happen to share a name must not collide.
        StableId.ForBrand("Samsung").Should().NotBe(StableId.ForStore("Samsung"));
    }
}
