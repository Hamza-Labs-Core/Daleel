using Daleel.Core.Models;
using Daleel.Pipeline.Extraction;
using FluentAssertions;
using Xunit;

namespace Daleel.Pipeline.Tests;

public class ProductIdentityTests
{
    [Theory]
    [InlineData("  012-345-678 ", "012345678")]
    [InlineData("abc123", "ABC123")]
    [InlineData(null, "")]
    public void NormalizeSku_StripsNonAlphanumericAndUppercases(string? sku, string expected) =>
        ProductIdentity.NormalizeSku(sku).Should().Be(expected);

    [Theory]
    [InlineData("012345678905", true)]  // 12-digit UPC
    [InlineData("ABC123", true)]        // exactly 6
    [InlineData("12345", false)]        // too short — likely a store code / placeholder
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasStrongSku_RequiresSixAlphanumerics(string? sku, bool expected) =>
        ProductIdentity.HasStrongSku(sku).Should().Be(expected);

    [Fact]
    public void DedupKey_WithoutStrongSku_IsByteIdenticalToBefore()
    {
        // The SKU fast-path must NOT change the key for items with no strong SKU — zero drift for the
        // (vast majority) non-SKU items, so routing/persistence stay exactly as before.
        ListingExtractor.DedupKey(new ProductListing { Brand = "Samsung", Model = "AR24TXHQ", Name = "x" })
            .Should().Be("m:samsung|ar24txhq");

        ListingExtractor.DedupKey(new ProductListing { Name = "Some Gadget" })
            .Should().Be("n:some gadget");

        // A too-short/junk code is not treated as a SKU → falls back to brand+model.
        ListingExtractor.DedupKey(new ProductListing { Sku = "12", Brand = "Samsung", Model = "AR24TXHQ", Name = "x" })
            .Should().Be("m:samsung|ar24txhq");
    }

    [Fact]
    public void DedupKey_WithStrongSku_GroupsSameProductAcrossStores()
    {
        // Two listings carrying the same global SKU — but different name/model text — collapse to one key.
        var a = ListingExtractor.DedupKey(new ProductListing { Sku = "0123-4567-8905", Name = "Samsung AR24 @ StoreA" });
        var b = ListingExtractor.DedupKey(new ProductListing
        {
            Sku = "012345678905", Brand = "Samsung", Model = "AR24 Split", Name = "totally different text"
        });

        a.Should().Be("k:012345678905");
        b.Should().Be(a, "the shared strong SKU keys both listings to the same model");
    }
}
