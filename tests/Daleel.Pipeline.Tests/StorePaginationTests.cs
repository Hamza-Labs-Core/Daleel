using Daleel.Pipeline.Extraction;
using FluentAssertions;
using Xunit;

namespace Daleel.Pipeline.Tests;

public class StorePaginationTests
{
    [Fact]
    public void SubstitutesExistingPageParam()
    {
        var pages = StorePagination.NextPages("https://store.com/c/laptops?page=1&sort=price", 3);

        pages.Should().Equal(
            "https://store.com/c/laptops?page=2&sort=price",
            "https://store.com/c/laptops?page=3&sort=price");
    }

    [Fact]
    public void SubstitutesStartOffset()
    {
        var pages = StorePagination.NextPages("https://store.com/search?q=ac&start=0", 3, perPage: 24);

        pages.Should().Equal(
            "https://store.com/search?q=ac&start=24",
            "https://store.com/search?q=ac&start=48");
    }

    [Fact]
    public void SubstitutesPathPageSegment()
    {
        var pages = StorePagination.NextPages("https://store.com/shop/page/1", 2);

        pages.Should().Equal("https://store.com/shop/page/2");
    }

    [Fact]
    public void AppendsPageParamWhenNoMarker()
    {
        StorePagination.NextPages("https://store.com/c/laptops", 3).Should().Equal(
            "https://store.com/c/laptops?page=2",
            "https://store.com/c/laptops?page=3");

        StorePagination.NextPages("https://store.com/c/laptops?sort=new", 2).Should().Equal(
            "https://store.com/c/laptops?sort=new&page=2");
    }

    [Fact]
    public void ReturnsEmptyForSinglePageOrNonHttp()
    {
        StorePagination.NextPages("https://store.com/c/laptops", 1).Should().BeEmpty();
        StorePagination.NextPages("ftp://store.com/x", 3).Should().BeEmpty();
        StorePagination.NextPages("not a url", 3).Should().BeEmpty();
    }
}
