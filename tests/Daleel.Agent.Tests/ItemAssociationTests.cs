using Daleel.Core.Geo;
using Daleel.Core.Models;
using Daleel.Search.Abstractions;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// The item-association pass of <see cref="AgentService.BuildProductSearchResultAsync"/>: research
/// that used to feed only the result's GLOBAL sections (stores/brands/reviews) is wired onto each
/// model's own card — the offer's store site, the brand's regional site, the model's page on the
/// brand site, and the reviews/mentions that name the model.
/// </summary>
public class ItemAssociationTests
{
    /// <summary>
    /// Builds the product result straight from a hand-made bundle — no LLM extraction, no
    /// reputation pass — so only the deterministic pipeline plus the association pass runs.
    /// </summary>
    private static Task<ProductSearchResult> BuildAsync(
        ResearchBundle bundle, string query = "espresso machines in Jordan")
    {
        var agent = new AgentService(new FakeLlmClient(_ => "unused"));
        return agent.BuildProductSearchResultAsync(
            query, GeoProfiles.Jordan, bundle, "summary", CancellationToken.None,
            assessReputation: false, useLlmExtraction: false);
    }

    private static SearchResult Shopping(string title, decimal price, string seller, string? url = null) => new()
    {
        Title = title, Price = new Money(price, "JOD"), Seller = seller, Url = url, Kind = SearchKind.Shopping
    };

    private static SearchResult Web(string title, string url, string snippet = "") => new()
    {
        Title = title, Url = url, Snippet = snippet, Kind = SearchKind.Web
    };

    [Fact]
    public async Task Offers_GainStoreUrl_FromStoresDictionary_ByNameAndByHost()
    {
        var bundle = new ResearchBundle
        {
            ShoppingResults = new[]
            {
                // No offer URL → only the store NAME ("Leaders" = seller) can link it to its store.
                Shopping("Krups XP801T10 Espresso Machine", 120, "Leaders"),
                // Seller "Smart Buy" ≠ store name "Smartbuy" (host label) → only the URL HOST matches.
                Shopping("DeLonghi EC685 Espresso Machine", 150, "Smart Buy", "https://smartbuy.com.jo/p/901")
            },
            WebResults = new[]
            {
                Web("Coffee Machines at Leaders Jordan", "https://leaders.jo/category/coffee-machines"),
                Web("Coffee Machines", "https://smartbuy.com.jo/category/coffee")
            }
        };

        var result = await BuildAsync(bundle);

        var krups = result.Models.Single(m => m.Name.Contains("Krups"));
        krups.Offers.Single().StoreUrl.Should().Be("https://leaders.jo/category/coffee-machines");

        var delonghi = result.Models.Single(m => m.Name.Contains("DeLonghi"));
        delonghi.Offers.Single().StoreUrl.Should().Be("https://smartbuy.com.jo/category/coffee");
        // The product-page link itself is untouched — StoreUrl is an ADDITIONAL link, not a rewrite.
        delonghi.Offers.Single().Url.Should().Be("https://smartbuy.com.jo/p/901");
    }

    [Fact]
    public async Task Model_GainsBrandRegionalUrl_FromMatchingBrandPage()
    {
        var bundle = new ResearchBundle
        {
            ShoppingResults = new[]
            {
                Shopping("Samsung WindFree Split AC", 450, "Leaders", "https://leaders.jo/p/ar18")
            },
            WebResults = new[]
            {
                // Classifies as a BrandPage; carries the market's /jo path → the regional site.
                Web("Air Conditioners | Samsung Jordan", "https://www.samsung.com/jo/air-conditioners/")
            }
        };

        var result = await BuildAsync(bundle, "ACs in Jordan");

        var samsung = result.Models.Single();
        samsung.Brand.Should().Be("Samsung");
        samsung.BrandRegionalUrl.Should().Be("https://www.samsung.com/jo/air-conditioners/");
    }

    [Fact]
    public async Task Model_GainsBrandSiteUrl_WhenBrandDomainPageNamesTheModel()
    {
        var bundle = new ResearchBundle
        {
            ShoppingResults = new[]
            {
                Shopping("Samsung WindFree Split AC", 450, "Leaders", "https://leaders.jo/p/ar18")
            },
            WebResults = new[]
            {
                // On the brand's own domain AND names the model → the model's brand-site page.
                Web("Samsung WindFree Split AC AR18TXHQ", "https://www.samsung.com/jo/air-conditioners/ar18txhq/")
            }
        };

        var result = await BuildAsync(bundle, "ACs in Jordan");

        var samsung = result.Models.Single();
        samsung.BrandSiteUrl.Should().Be("https://www.samsung.com/jo/air-conditioners/ar18txhq/");
        // A link already surfaced as the brand-site page is never duplicated as a mention.
        samsung.Mentions.Should().NotContain(l => l.Url.Contains("samsung.com"));
    }

    [Fact]
    public async Task Review_NamingModel_AttachesToThatModelOnly()
    {
        var bundle = new ResearchBundle
        {
            ShoppingResults = new[]
            {
                Shopping("Krups XP801T10 Espresso Machine", 120, "Leaders", "https://leaders.jo/p/1"),
                Shopping("DeLonghi EC685 Espresso Machine", 150, "Leaders", "https://leaders.jo/p/2")
            },
            WebResults = new[]
            {
                Web("Krups XP801T10 Review",
                    "https://coffeegeek.com/krups-xp801t10-review",
                    "This Krups espresso machine impresses with fast heat-up")
            }
        };

        var result = await BuildAsync(bundle);

        var krups = result.Models.Single(m => m.Name.Contains("Krups"));
        krups.Reviews.Should().ContainSingle(r =>
            r.Url == "https://coffeegeek.com/krups-xp801t10-review" && r.Title == "Krups XP801T10 Review");
        // A review attached to the model's card is never repeated as one of its mentions.
        krups.Mentions.Should().NotContain(l => l.Url.Contains("coffeegeek"));

        var delonghi = result.Models.Single(m => m.Name.Contains("DeLonghi"));
        delonghi.Reviews.Should().BeEmpty();
        delonghi.Mentions.Should().BeEmpty();

        // The global "Related articles" section still lists the article.
        result.Reviews.Should().Contain(r => (r.Url ?? "").Contains("coffeegeek"));
    }

    [Fact]
    public async Task WebResult_MentioningModel_BecomesMentionOnItsCard()
    {
        var bundle = new ResearchBundle
        {
            ShoppingResults = new[]
            {
                Shopping("Krups XP801T10 Espresso Machine", 120, "Leaders", "https://leaders.jo/p/1")
            },
            WebResults = new[]
            {
                // A reddit thread naming the model: non-commerce, so it never becomes a store or
                // offer — an item MENTION is exactly where it belongs.
                Web("Is the Krups XP801T10 worth it for espresso?",
                    "https://www.reddit.com/r/espresso/comments/abc123")
            }
        };

        var result = await BuildAsync(bundle);

        var krups = result.Models.Single();
        krups.Mentions.Should().ContainSingle(l =>
            l.Url == "https://www.reddit.com/r/espresso/comments/abc123" &&
            l.Title == "Is the Krups XP801T10 worth it for espresso?");
    }

    [Fact]
    public async Task UnrelatedReview_AttachesToNoModel()
    {
        var bundle = new ResearchBundle
        {
            ShoppingResults = new[]
            {
                Shopping("Krups XP801T10 Espresso Machine", 120, "Leaders", "https://leaders.jo/p/1")
            },
            WebResults = new[]
            {
                Web("Best dishwashers in Jordan 2026 - buying guide",
                    "https://blog.example.com/dishwashers",
                    "top dishwasher picks for every budget")
            }
        };

        var result = await BuildAsync(bundle);

        var krups = result.Models.Single();
        krups.Reviews.Should().BeEmpty();
        krups.Mentions.Should().BeEmpty();

        // Unattached, it still surfaces in the result-level articles section — never lost.
        result.Reviews.Should().Contain(r => (r.Url ?? "").Contains("dishwashers"));
    }
}
