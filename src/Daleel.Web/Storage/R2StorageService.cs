using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
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

    /// <summary>
    /// Uploads a JSON document to R2 at the human-readable object key <paramref name="objectKey"/> (e.g.
    /// <c>site-data/samsung/galaxy-s24/brand-site.json</c>) and returns the hosted URL. Used to persist
    /// raw scraped specs and the final canonical spec sheet outside the database. Returns null when R2 is
    /// not configured, the input is empty, or anything goes wrong — never throws.
    /// </summary>
    Task<string?> StoreJsonAsync(string json, string objectKey, CancellationToken ct = default);
}

/// <summary>No-op storage used when R2 is unconfigured: keeps the original URL so images still render.</summary>
public sealed class NullR2StorageService : IR2StorageService
{
    public bool IsConfigured => false;

    public Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default) =>
        Task.FromResult(sourceUrl);

    // No bucket to write to and no public host to serve from, so there is no URL to return.
    public Task<string?> StoreJsonAsync(string json, string objectKey, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
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

    /// <summary>Cap on a spec-sheet JSON blob (4 MB is far beyond any real spec document).</summary>
    private const long MaxJsonBytes = 4 * 1024 * 1024;

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

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            {
                return sourceUrl;
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

    public async Task<string?> StoreJsonAsync(string json, string objectKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(objectKey))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxJsonBytes)
        {
            _logger.LogDebug("R2 JSON store skipped {Key}: {Bytes} bytes exceeds cap", objectKey, bytes.Length);
            return null;
        }

        var key = NormalizeObjectKey(objectKey);

        try
        {
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = new MemoryStream(bytes),
                ContentType = "application/json",
                // R2 rejects AWS SigV4 streaming payload signing — same fix as the image path.
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
            // Best-effort: a failed upload must never break the spec pipeline — the DB copy remains canonical.
            _logger.LogDebug(ex, "R2 JSON store failed for {Key}", key);
            return null;
        }
    }

    /// <summary>
    /// Sanitizes a slash-delimited object key into a safe, lower-cased R2 path: each segment is
    /// whitespace-collapsed, illegal characters are replaced with '-', and the (.json) extension is kept.
    /// </summary>
    internal static string NormalizeObjectKey(string objectKey)
    {
        var segments = objectKey.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = segments.Select(NormalizeSegment).Where(s => s.Length > 0);
        return string.Join('/', safe);
    }

    private static string NormalizeSegment(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        var lastDash = false;
        foreach (var ch in segment.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_')
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        return sb.ToString().Trim('-');
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
