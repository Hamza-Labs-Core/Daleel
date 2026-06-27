using System.Net;
using Daleel.Search.Http;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests.Http;

/// <summary>
/// The SSRF guard is the single chokepoint for fetching attacker-influenced URLs (scraped/LLM image
/// links, guessed domains). These tests pin the IP classification and the two pre-flight surfaces: the
/// DNS-free literal check used for vendor-edge scrapes, and the scheme handling. All inputs use IP
/// literals or syntactic checks so nothing here touches the network.
/// </summary>
public class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]      // loopback
    [InlineData("127.1.2.3")]      // loopback /8
    [InlineData("10.0.0.5")]       // RFC1918
    [InlineData("172.16.0.1")]     // RFC1918 /12 low
    [InlineData("172.31.255.254")] // RFC1918 /12 high
    [InlineData("192.168.1.1")]    // RFC1918
    [InlineData("169.254.169.254")] // link-local — the cloud metadata endpoint
    [InlineData("100.64.0.1")]     // CGNAT
    [InlineData("0.0.0.0")]        // "this host"
    [InlineData("::1")]            // IPv6 loopback
    [InlineData("fe80::1")]        // IPv6 link-local
    [InlineData("fc00::1")]        // IPv6 ULA
    [InlineData("fd12:3456::1")]   // IPv6 ULA (fd)
    [InlineData("::ffff:10.0.0.1")] // IPv4-mapped RFC1918
    public void IsBlocked_FlagsPrivateAndInternalAddresses(string ip)
    {
        SsrfGuard.IsBlocked(IPAddress.Parse(ip)).Should().BeTrue();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]   // just outside RFC1918 /12
    [InlineData("100.128.0.1")]  // just outside CGNAT /10
    [InlineData("2606:4700:4700::1111")] // public IPv6 (Cloudflare)
    public void IsBlocked_AllowsPublicAddresses(string ip)
    {
        SsrfGuard.IsBlocked(IPAddress.Parse(ip)).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata SSRF payload
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("https://10.0.0.5/internal")]
    [InlineData("http://[::1]/")]
    [InlineData("http://localhost/")]
    [InlineData("https://db.internal/")]
    [InlineData("http://service.local/")]
    [InlineData("ftp://example.com/file")]  // non-http scheme
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSafePublicUrl_RejectsUnsafeOrInternalTargets(string? url)
    {
        SsrfGuard.IsSafePublicUrl(url).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://store.jo/ac")]
    [InlineData("https://samsung.com/jo/ar24")]
    [InlineData("http://8.8.8.8/")]  // public IP literal is fine
    public void IsSafePublicUrl_AllowsPublicHttpUrls(string url)
    {
        SsrfGuard.IsSafePublicUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://169.254.169.254/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("ftp://example.com/")]
    public async Task IsSafePublicUrlAsync_RejectsBlockedLiteralsAndSchemes(string url)
    {
        // IP-literal hosts need no DNS, so this stays offline.
        (await SsrfGuard.IsSafePublicUrlAsync(url)).Should().BeFalse();
    }

    [Fact]
    public async Task IsSafePublicUrlAsync_AllowsPublicLiteral()
    {
        (await SsrfGuard.IsSafePublicUrlAsync("https://1.1.1.1/")).Should().BeTrue();
    }
}
