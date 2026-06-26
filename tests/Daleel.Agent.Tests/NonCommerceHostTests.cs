using Daleel.Agent;
using FluentAssertions;
using Xunit;

namespace Daleel.Agent.Tests;

/// <summary>
/// The sanity filter that stops social/forum/reference sites being surfaced as stores or brands —
/// "is reddit a store in Jordan?" No. Matches the registrable host and any subdomain, leaves real
/// commerce hosts (and bare/invalid input) alone.
/// </summary>
public class NonCommerceHostTests
{
    [Theory]
    [InlineData("https://www.reddit.com/r/jordan/comments/xyz", true)]
    [InlineData("https://jo.reddit.com/r/x", true)]          // subdomain
    [InlineData("https://youtube.com/watch?v=1", true)]
    [InlineData("https://m.facebook.com/page", true)]
    [InlineData("https://en.wikipedia.org/wiki/Air_conditioning", true)]
    [InlineData("https://x.com/someone", true)]
    [InlineData("https://leaders.jo/product/lg-ac", false)]  // real store
    [InlineData("https://shop.samsung.com/jo/...", false)]   // real brand store
    [InlineData("https://souqprice.com/jo/...", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNonCommerceHost_FlagsSocialAndForumHostsOnly(string? url, bool expected)
    {
        AgentService.IsNonCommerceHost(url).Should().Be(expected);
    }
}
