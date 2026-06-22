using Daleel.Core.Models;
using Daleel.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Core.Tests.Pipeline;

public class PostDeduplicatorTests
{
    private static SocialPost Post(string id, string text) => new() { Id = id, Text = text };

    [Fact]
    public void IsUnique_FirstSighting_IsTrue()
    {
        var dedup = new PostDeduplicator();
        dedup.IsUnique(Post("1", "شركة الاتصالات")).Should().BeTrue();
    }

    [Fact]
    public void IsUnique_IdenticalText_SecondSightingIsFalse()
    {
        var dedup = new PostDeduplicator();
        dedup.IsUnique(Post("1", "شركة الاتصالات")).Should().BeTrue();
        dedup.IsUnique(Post("2", "شركة الاتصالات")).Should().BeFalse();
    }

    [Fact]
    public void IsUnique_OrthographicVariants_CollapseToDuplicate()
    {
        // Same words, different diacritics → normalized hash matches → duplicate.
        var dedup = new PostDeduplicator();
        dedup.IsUnique(Post("1", "شَرِكَة الاتصالات")).Should().BeTrue();
        dedup.IsUnique(Post("2", "شركة الاتصالات")).Should().BeFalse();
    }

    [Fact]
    public void Deduplicate_PreservesOrderAndRemovesDuplicates()
    {
        var dedup = new PostDeduplicator();
        var input = new[]
        {
            Post("1", "خبر أول"),
            Post("2", "خبر ثاني"),
            Post("3", "خبر أول"),   // dup of 1
            Post("4", "خبر ثالث"),
        };

        var result = dedup.Deduplicate(input);

        result.Should().HaveCount(3);
        result.Select(p => p.Id).Should().ContainInOrder("1", "2", "4");
    }

    [Fact]
    public void UniqueCount_TracksDistinctPosts()
    {
        var dedup = new PostDeduplicator();
        dedup.Deduplicate(new[]
        {
            Post("1", "أ"),
            Post("2", "ب"),
            Post("3", "أ"),
        });

        dedup.UniqueCount.Should().Be(2);
    }

    [Fact]
    public void Reset_ClearsSeenHashes()
    {
        var dedup = new PostDeduplicator();
        dedup.IsUnique(Post("1", "نص")).Should().BeTrue();
        dedup.Reset();
        dedup.IsUnique(Post("2", "نص")).Should().BeTrue();
    }

    [Fact]
    public void ComputeHash_IsStableAndHex()
    {
        var h1 = PostDeduplicator.ComputeHash("شركة");
        var h2 = PostDeduplicator.ComputeHash("شركة");

        h1.Should().Be(h2);
        h1.Should().HaveLength(64);
        h1.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeHash_NormalizedEquivalents_ShareHash()
    {
        PostDeduplicator.ComputeHash("شَرِكَة")
            .Should().Be(PostDeduplicator.ComputeHash("شركه"));
    }
}
