using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Daleel.Search.Http;
using Daleel.Web.Logging;

namespace Daleel.Web.Storage;

/// <summary>
/// Stores remote images in Cloudflare R2 (S3-compatible) and returns a stable URL to persist instead of
/// hot-linking the original. The whole feature is best-effort and optional: every method degrades to the
/// original URL when R2 isn't configured or a transfer fails, so the caller can always just persist what
/// it gets back.
/// </summary>
public interface IR2StorageService
{
    /// <summary>True when R2 credentials are present and uploads will be attempted.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Downloads <paramref name="sourceUrl"/> and uploads it to R2 under <paramref name="keyPrefix"/>,
    /// returning the hosted URL. Returns the original <paramref name="sourceUrl"/> unchanged when R2 is
    /// not configured, the URL is empty/already R2-hosted, or anything goes wrong — never throws.
    /// </summary>
    Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default);
}

/// <summary>No-op storage used when R2 is unconfigured: keeps the original URL so images still render.</summary>
public sealed class NullR2StorageService : IR2StorageService
{
    public bool IsConfigured => false;

    public Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default) =>
        Task.FromResult(sourceUrl);
}

public sealed class R2StorageService : IR2StorageService, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly HttpClient _http;
    private readonly string _bucket;
    private readonly string _publicBaseUrl;
    private readonly ILogger<R2StorageService> _logger;

    /// <summary>Cap on a single image we'll pull into memory before uploading (8 MB covers product shots).</summary>
    private const long MaxImageBytes = 8 * 1024 * 1024;

    public bool IsConfigured => true;

    public R2StorageService(R2LoggingOptions options, string publicBaseUrl, HttpClient http,
        ILogger<R2StorageService> logger)
    {
        _bucket = options.BucketName;
        _http = http;
        _logger = logger;
        // Trailing slash trimmed so we can join with "/{key}" without doubling it.
        _publicBaseUrl = publicBaseUrl.TrimEnd('/');

        var config = new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            // R2 is path-style only (bucket in the path, not a vhost subdomain).
            ForcePathStyle = true
        };
        _s3 = new AmazonS3Client(options.AccessKey, options.SecretKey, config);
    }

    /// <summary>Test/DI seam: inject a fake <see cref="IAmazonS3"/> and <see cref="HttpClient"/>.</summary>
    public R2StorageService(IAmazonS3 s3, HttpClient http, string bucket, string publicBaseUrl,
        ILogger<R2StorageService> logger)
    {
        _s3 = s3;
        _http = http;
        _bucket = bucket;
        _publicBaseUrl = publicBaseUrl.TrimEnd('/');
        _logger = logger;
    }

    public async Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) ||
            !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return sourceUrl;
        }

        // Already hosted by us — nothing to copy, and re-uploading would loop.
        if (sourceUrl.StartsWith(_publicBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            return sourceUrl;
        }

        // SSRF guard: sourceUrl comes from scraped pages / LLM-extracted image fields, and the fetch
        // below runs from our own host. Refuse private/internal targets pre-flight (the injected client
        // is also connect-time guarded, so a DNS rebind after this check is still blocked). Best-effort:
        // a blocked URL degrades to the original rather than throwing.
        if (!await SsrfGuard.IsSafePublicUrlAsync(sourceUrl, ct).ConfigureAwait(false))
        {
            _logger.LogDebug("R2 image store skipped {Url}: blocked by SSRF guard", sourceUrl);
            return sourceUrl;
        }

        // Deterministic key: same source image always maps to the same object, so re-running enrichment
        // overwrites in place rather than piling up duplicates, and the stored URL is stable.
        var key = BuildKey(keyPrefix, sourceUrl, uri);

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return sourceUrl;
            }

            if (response.Content.Headers.ContentLength is > MaxImageBytes)
            {
                return sourceUrl;
            }

            // Cap the read at the stream level: ContentLength can be absent (chunked transfer) or lie, and
            // ReadAsByteArrayAsync would otherwise buffer the entire body of this attacker-influenced fetch
            // into memory before any size check.
            var bytes = await ReadCappedAsync(response.Content, MaxImageBytes, ct).ConfigureAwait(false);
            if (bytes is not { Length: > 0 })
            {
                return sourceUrl; // empty body, or exceeded the size cap mid-stream
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? ContentTypeFor(key);

            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = new MemoryStream(bytes),
                ContentType = contentType,
                // R2 rejects AWS SigV4 streaming payload signing — disabling it is the documented fix for
                // S3-compatible PUTs to R2 (the same knob the Serilog AmazonS3 sink sets).
                DisablePayloadSigning = true
            }, ct).ConfigureAwait(false);

            return $"{_publicBaseUrl}/{key}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: a failed upload must never break enrichment — fall back to the original URL.
            _logger.LogDebug(ex, "R2 image store failed for {Url}; keeping original", sourceUrl);
            return sourceUrl;
        }
    }

    /// <summary>
    /// Reads an HTTP body into memory but never buffers more than <paramref name="maxBytes"/>. Guards the
    /// image-fetch path against a missing/lying Content-Length (e.g. chunked transfer) — returns null as
    /// soon as the cap is crossed rather than reading the rest. Memory use is bounded to maxBytes + one
    /// read buffer.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null; // over the cap — stop without buffering the remainder
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Builds a collision-resistant, extension-preserving object key from the source URL.</summary>
    internal static string BuildKey(string keyPrefix, string sourceUrl, Uri uri)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl)))[..16].ToLowerInvariant();
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5)
        {
            ext = ".jpg";
        }

        var prefix = keyPrefix.Trim('/');
        return string.IsNullOrEmpty(prefix) ? $"{hash}{ext}" : $"{prefix}/{hash}{ext}";
    }

    private static string ContentTypeFor(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        _ => "image/jpeg"
    };

    public void Dispose() => _s3.Dispose();
}
