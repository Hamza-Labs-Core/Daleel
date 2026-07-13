using Daleel.Web.Storage;

namespace Daleel.Web.Logging;

/// <summary>
/// Mirrors the Serilog File sink's newest local day file (<c>daleel-YYYYMMDD.jsonl</c>) to the R2
/// Logs bucket as <c>logs/&lt;filename&gt;</c> — the complete file, re-uploaded whenever it grows.
/// </summary>
/// <remarks>
/// This replaces the Serilog AmazonS3 sink, which uploaded ONLY each flush batch under one fixed
/// day key: every upload clobbered the previous object, so the R2 "day file" held just the last few
/// seconds of logs and every log search came back empty (the 702-byte day files). The local File
/// sink already appends correctly and rolls daily; uploading it wholesale makes the R2 object the
/// full day so far, idempotently. A length watermark skips no-change uploads; the cap bounds the
/// re-upload cost if a day ever gets pathologically chatty.
/// </remarks>
public sealed class LogFileMirror
{
    /// <summary>Beyond this the mirror stops re-uploading (a runaway log day shouldn't melt egress).</summary>
    private const long MaxBytes = 48 * 1024 * 1024;

    private readonly string _directory;
    private readonly IR2StorageService _r2;
    private string? _lastFile;
    private long _lastLength = -1;

    public LogFileMirror(string directory, IR2StorageService r2)
        => (_directory, _r2) = (directory, r2);

    /// <summary>Uploads the newest day file when it changed; returns whether an upload happened.</summary>
    public async Task<bool> MirrorOnceAsync(CancellationToken ct)
    {
        if (!_r2.IsConfigured || !Directory.Exists(_directory))
        {
            return false;
        }

        // The newest daleel-*.jsonl is the live day file (the File sink rolls daily; name sorts by date).
        var file = Directory.EnumerateFiles(_directory, "daleel-*.jsonl")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (file is null)
        {
            return false;
        }

        var length = new FileInfo(file).Length;
        if (length is 0 or > MaxBytes || (file == _lastFile && length == _lastLength))
        {
            return false; // empty, capped, or unchanged since the last mirror
        }

        // Share the file with the still-writing sink (ReadWrite share), and read the whole thing —
        // a partial trailing line from a concurrent write is harmless in a line-oriented grep target.
        string content;
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var key = $"logs/{Path.GetFileName(file)}";
        // The honest bool API (StoreJsonAsync returns null on SUCCESS for the private Logs bucket and
        // caps at 4MB): false means the upload failed and the watermark must not advance.
        if (!await _r2.StoreLogFileAsync(content, key, ct).ConfigureAwait(false))
        {
            return false;
        }

        (_lastFile, _lastLength) = (file, length);
        return true;
    }
}
