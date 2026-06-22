using Daleel.Search.Abstractions;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

public class OpenSooqProviderTests
{
    /// <summary>Returns canned markdown regardless of URL.</summary>
    private sealed class FakeScraper : IScrapeProvider
    {
        private readonly string _markdown;
        public string LastUrl { get; private set; } = string.Empty;
        public string Name => "fake-scraper";
        public FakeScraper(string markdown) => _markdown = markdown;

        public Task<ScrapedPage> ScrapeAsync(string url, ScrapeFormat format = ScrapeFormat.Markdown, CancellationToken ct = default)
        {
            LastUrl = url;
            return Task.FromResult(new ScrapedPage { Url = url, Content = _markdown, Success = true, Provider = Name });
        }
    }

    [Fact]
    public void BuildSearchUrl_UsesCountrySubdomain()
    {
        OpenSooqProvider.BuildSearchUrl("مكيف", "jo").Should().StartWith("https://jo.opensooq.com");
        OpenSooqProvider.BuildSearchUrl("AC", "sa").Should().StartWith("https://sa.opensooq.com");
    }

    [Fact]
    public async Task SearchAsync_ExtractsListingsWithPrices()
    {
        var markdown = """
            # Results for مكيف
            [مكيف سامسونج 24000 وحدة](https://jo.opensooq.com/ar/123) - 450 دينار
            [مكيف ال جي سبليت](https://jo.opensooq.com/ar/456) - 599 دينار
            Some unrelated [home page](https://example.com) link.
            """;

        var provider = new OpenSooqProvider(new FakeScraper(markdown), "JOD");
        var listings = await provider.SearchAsync("مكيف", "jo");

        listings.Should().HaveCount(2);
        listings[0].Url.Should().Contain("opensooq.com");
        listings[0].Price.Amount.Should().Be(450);
        listings[0].Price.Currency.Should().Be("JOD");
        listings[1].Price.Amount.Should().Be(599);
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesByUrl()
    {
        var markdown = """
            [مكيف](https://jo.opensooq.com/ar/1) 450 دينار
            [مكيف نفس الرابط](https://jo.opensooq.com/ar/1) 450 دينار
            """;

        var provider = new OpenSooqProvider(new FakeScraper(markdown));
        var listings = await provider.SearchAsync("مكيف", "jo");

        listings.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_EmptyContent_ReturnsEmpty()
    {
        var provider = new OpenSooqProvider(new FakeScraper(string.Empty));
        var listings = await provider.SearchAsync("مكيف", "jo");
        listings.Should().BeEmpty();
    }
}
