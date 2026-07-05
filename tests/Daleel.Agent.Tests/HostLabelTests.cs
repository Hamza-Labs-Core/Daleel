using Daleel.Agent;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// Pins the store/brand-name-from-host rule: the REGISTRABLE label, never a subdomain. Taking the
/// first host label minted country/language subdomains as store names ("Jo" from jo.opensooq.com).
/// </summary>
public class HostLabelTests
{
    [Theory]
    [InlineData("https://jo.opensooq.com/en/coffee", "Opensooq")]     // country subdomain ≠ store
    [InlineData("https://jordan.dubizzle.com/x", "Dubizzle")]
    [InlineData("https://www.samsung.com/jo", "Samsung")]
    [InlineData("https://m.facebook.com/somestore", "Facebook")]
    [InlineData("https://leaders.jo/product/x", "Leaders")]           // cc TLD, no subdomain
    [InlineData("https://khaleej.com.sa/shop", "Khaleej")]            // generic+cc two-label suffix
    [InlineData("https://shop.emirates.store.example.co.uk/a", "Example")] // deep subdomains
    [InlineData("https://amazon.co.uk/dp/1", "Amazon")]
    [InlineData("https://belmio.jo/", "Belmio")]
    public void Registrable_label_wins_over_subdomains(string url, string expected) =>
        AgentService.HostLabel(url).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void Garbage_yields_null(string? url) =>
        AgentService.HostLabel(url).Should().BeNull();
}
