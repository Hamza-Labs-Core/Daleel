using Daleel.Web.Logging;
using Daleel.Web.Storage;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Logging;

// The R2 log mirror replaces the Serilog AmazonS3 sink, which re-uploaded ONLY each flush batch
// under the same day key — every upload clobbered the previous one, so the R2 "day file" held just
// the last few seconds of logs (the 702-byte mystery). The mirror uploads the local File sink's
// complete, appending day file whenever it grows, so the R2 object is always the full day so far.
public class LogFileMirrorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("logmirror").FullName;

    private sealed class FakeR2 : IR2StorageService
    {
        public List<(string Key, string Content, R2Bucket Bucket)> Uploads { get; } = new();
        public bool IsConfigured => true;
        // The mirror must use the honest bool API: StoreJsonAsync returns null on SUCCESS for a
        // private bucket (Logs is private) and caps at 4MB — both would break the watermark.
        public Task<bool> StoreLogFileAsync(string content, string objectKey, CancellationToken ct = default)
        {
            Uploads.Add((objectKey, content, R2Bucket.Logs));
            return Task.FromResult(true);
        }
        public Task<string?> StoreJsonAsync(string json, string objectKey, R2Bucket bucket = R2Bucket.Specs, CancellationToken ct = default) =>
            throw new NotSupportedException("mirror must not use the null-ambiguous JSON path");
        public Task<R2BucketHealth> ProbeBucketAsync(R2Bucket bucket, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<R2Listing> ListObjectsAsync(string? prefix, string? continuationToken = null, int maxKeys = 200, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<R2ObjectText?> ReadTextAsync(string key, long maxBytes = 262144, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) => throw new NotSupportedException();
        public string? DownloadUrl(string key, R2Bucket bucket = R2Bucket.Data, TimeSpan? expiry = null) => null;
    }

    [Fact]
    public async Task Uploads_the_newest_day_file_in_full_under_the_logs_prefix()
    {
        File.WriteAllText(Path.Combine(_dir, "daleel-20260712.jsonl"), "old-day\n");
        File.WriteAllText(Path.Combine(_dir, "daleel-20260713.jsonl"), "line1\nline2\n");
        var r2 = new FakeR2();
        var mirror = new LogFileMirror(_dir, r2);

        (await mirror.MirrorOnceAsync(default)).Should().BeTrue();

        var up = r2.Uploads.Should().ContainSingle().Subject;
        up.Key.Should().Be("logs/daleel-20260713.jsonl", "the newest day file is the live one");
        up.Content.Should().Be("line1\nline2\n", "the WHOLE file must land, not a batch");
        up.Bucket.Should().Be(R2Bucket.Logs);
    }

    [Fact]
    public async Task Skips_when_the_file_has_not_grown_and_reuploads_when_it_has()
    {
        var path = Path.Combine(_dir, "daleel-20260713.jsonl");
        File.WriteAllText(path, "line1\n");
        var r2 = new FakeR2();
        var mirror = new LogFileMirror(_dir, r2);

        (await mirror.MirrorOnceAsync(default)).Should().BeTrue();
        (await mirror.MirrorOnceAsync(default)).Should().BeFalse("unchanged file must not re-upload");

        File.AppendAllText(path, "line2\n");
        (await mirror.MirrorOnceAsync(default)).Should().BeTrue("growth re-mirrors the full file");

        r2.Uploads.Should().HaveCount(2);
        r2.Uploads[^1].Content.Should().Be("line1\nline2\n");
    }

    [Fact]
    public async Task Missing_directory_or_no_day_files_is_a_quiet_noop()
    {
        var mirror = new LogFileMirror(Path.Combine(_dir, "nope"), new FakeR2());
        (await mirror.MirrorOnceAsync(default)).Should().BeFalse();
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }
}
