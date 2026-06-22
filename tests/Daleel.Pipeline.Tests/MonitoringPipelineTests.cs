using Daleel.Core.Arabic;
using Daleel.Core.Models;
using Daleel.Core.Pipeline;
using Daleel.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Pipeline.Tests;

public class MonitoringPipelineTests
{
    /// <summary>A fetcher that returns canned posts per source — no network involved.</summary>
    private sealed class FakeFetcher : IPostFetcher
    {
        private readonly IReadOnlyList<SocialPost> _posts;
        public FakeFetcher(IReadOnlyList<SocialPost> posts) => _posts = posts;

        public Task<IReadOnlyList<SocialPost>> FetchAsync(
            Source source, string? keyword = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_posts);
    }

    /// <summary>An in-memory writer that captures everything the pipeline emits.</summary>
    private sealed class CollectingWriter : IResultWriter
    {
        public List<MatchedPost> Written { get; } = new();

        public Task WriteAsync(MatchedPost result, CancellationToken cancellationToken = default)
        {
            Written.Add(result);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static MonitoringJob Job(params string[] keywords) => new()
    {
        Name = "test",
        Keywords = keywords,
        Mode = MatchMode.Contains,
        Sources = new[] { new Source { Name = "fake", Kind = SourceKind.Search, Target = keywords.FirstOrDefault() ?? "x" } }
    };

    [Fact]
    public async Task RunAsync_WritesOnlyMatchingPosts()
    {
        var posts = new[]
        {
            new SocialPost { Id = "1", Text = "أعلنت شركة الاتصالات عن خدمة جديدة" }, // match
            new SocialPost { Id = "2", Text = "القطة تجلس على الطاولة" },             // no match
            new SocialPost { Id = "3", Text = "نتائج الشَّرِكَة الفصلية ممتازة" },       // match (diacritics)
        };

        var writer = new CollectingWriter();
        var pipeline = new MonitoringPipeline(new FakeFetcher(posts), new ArabicMatcher(), writer);

        var report = await pipeline.RunAsync(Job("شركة"));

        report.PostsFetched.Should().Be(3);
        report.Matches.Should().Be(2);
        writer.Written.Select(w => w.Post.Id).Should().BeEquivalentTo(new[] { "1", "3" });
    }

    [Fact]
    public async Task RunAsync_DeduplicatesMatchingPosts()
    {
        var posts = new[]
        {
            new SocialPost { Id = "1", Text = "شركة الاتصالات الوطنية" },
            new SocialPost { Id = "2", Text = "شَرِكَة الاتصالات الوطنيّة" }, // normalizes identical → dup
        };

        var writer = new CollectingWriter();
        var pipeline = new MonitoringPipeline(new FakeFetcher(posts), new ArabicMatcher(), writer);

        var report = await pipeline.RunAsync(Job("شركة"));

        report.Matches.Should().Be(1);
        report.Duplicates.Should().Be(1);
        writer.Written.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunAsync_NoMatches_WritesNothing()
    {
        var posts = new[] { new SocialPost { Id = "1", Text = "نص لا علاقة له بالموضوع" } };
        var writer = new CollectingWriter();
        var pipeline = new MonitoringPipeline(new FakeFetcher(posts), new ArabicMatcher(), writer);

        var report = await pipeline.RunAsync(Job("شركة"));

        report.Matches.Should().Be(0);
        writer.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_CarriesMatchMetadataToWriter()
    {
        var posts = new[] { new SocialPost { Id = "1", Text = "أخبار عاجلة من الوزارة" } };
        var writer = new CollectingWriter();
        var pipeline = new MonitoringPipeline(new FakeFetcher(posts), new ArabicMatcher(), writer);

        await pipeline.RunAsync(Job("أخبار"));

        writer.Written.Should().ContainSingle();
        var match = writer.Written[0].Match;
        match.IsMatch.Should().BeTrue();
        match.MatchedKeyword.Should().Be("أخبار");
        match.Context.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunAsync_JsonlWriter_ProducesOneLinePerMatch()
    {
        var posts = new[]
        {
            new SocialPost { Id = "1", Text = "شركة الاتصالات" },
            new SocialPost { Id = "2", Text = "خبر عن الشركة الثانية" },
        };

        var tempFile = Path.Combine(Path.GetTempPath(), $"daleel-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new JsonlResultWriter(tempFile))
            {
                var pipeline = new MonitoringPipeline(new FakeFetcher(posts), new ArabicMatcher(), writer);
                await pipeline.RunAsync(Job("شركة"));
            }

            var lines = await File.ReadAllLinesAsync(tempFile);
            lines.Should().HaveCount(2);
            lines.Should().AllSatisfy(l => l.Should().StartWith("{").And.EndWith("}"));
            // Arabic should be written un-escaped.
            string.Join("\n", lines).Should().Contain("شركة");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
