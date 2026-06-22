using System.Text;
using System.Text.Json;
using Daleel.Core.Pipeline;

namespace Daleel.Pipeline;

/// <summary>
/// Writes matched posts to a JSON Lines (<c>.jsonl</c>) file — one self-contained JSON
/// object per line. JSONL is append-friendly and stream-readable, which suits a
/// monitoring tool that emits results incrementally and may be tailed live.
/// </summary>
public sealed class JsonlResultWriter : IResultWriter
{
    private readonly StreamWriter _writer;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonlResultWriter(string path, bool append = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(path, append, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };

        _jsonOptions = new JsonSerializerOptions
        {
            // Keep Arabic readable in the output rather than \u-escaping every glyph.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };
    }

    public async Task WriteAsync(MatchedPost result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var line = JsonSerializer.Serialize(result, _jsonOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
