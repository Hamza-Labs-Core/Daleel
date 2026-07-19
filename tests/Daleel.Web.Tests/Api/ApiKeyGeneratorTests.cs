using Daleel.Web.Api;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Api;

/// <summary>
/// The B2B key format and its hash round-trip: <c>dlk_live_&lt;43 url-safe chars&gt;</c>, SHA-256 hex
/// as the only stored form, and a display prefix that identifies without revealing.
/// </summary>
public class ApiKeyGeneratorTests
{
    [Fact]
    public void Generate_ProducesLiveKeyWith43UrlSafeChars()
    {
        var key = ApiKeyGenerator.Generate();

        key.FullKey.Should().StartWith("dlk_live_");
        var body = key.FullKey["dlk_live_".Length..];
        body.Should().HaveLength(43); // 32 bytes base64url, no padding
        body.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
    }

    [Fact]
    public void Generate_HashRoundTrips_SoTheStoredHashVerifiesThePresentedKey()
    {
        var key = ApiKeyGenerator.Generate();

        // Verification is hash-and-compare: hashing the presented full key must reproduce the
        // stored hash exactly.
        ApiKeyGenerator.Hash(key.FullKey).Should().Be(key.Hash);
        key.Hash.Should().MatchRegex("^[0-9a-f]{64}$"); // lower-case SHA-256 hex
    }

    [Fact]
    public void Generate_PrefixIsTheDisplayHeadOfTheKey_NotTheSecret()
    {
        var key = ApiKeyGenerator.Generate();

        key.FullKey.Should().StartWith(key.Prefix);
        // "dlk_live_" + 8 chars of the random part: enough to tell keys apart, never enough to guess.
        key.Prefix.Should().HaveLength(17);
    }

    [Fact]
    public void Generate_NeverRepeats()
    {
        var a = ApiKeyGenerator.Generate();
        var b = ApiKeyGenerator.Generate();

        a.FullKey.Should().NotBe(b.FullKey);
        a.Hash.Should().NotBe(b.Hash);
    }

    [Fact]
    public void Hash_DiffersForDifferentKeys_AndIsDeterministic()
    {
        ApiKeyGenerator.Hash("dlk_live_aaa").Should().Be(ApiKeyGenerator.Hash("dlk_live_aaa"));
        ApiKeyGenerator.Hash("dlk_live_aaa").Should().NotBe(ApiKeyGenerator.Hash("dlk_live_aab"));
    }
}
