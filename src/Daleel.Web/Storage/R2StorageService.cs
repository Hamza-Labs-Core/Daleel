using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Daleel.Search.Http;

namespace Daleel.Web.Storage;

/// <summary>
/// Stores remote images in Cloudflare R2 (S3-compatible) and returns a stable URL to persist instead of
/// hot-linking the original. The whole feature is best-effort and optional: every method degrades to the
/// original URL when R2 isn't configured or a transfer fails, so the caller can always just persist what
/// it gets back.
/// </summary>
/// <summary>One stored object: its full key, byte size, and last-modified time.</summary>
public sealed record R2Object(string Key, long Size, DateTimeOffset LastModified);

/// <summary>
/// One delimited page of a bucket listing: the sub-"folders" (<paramref name="Prefixes"/>) and the files
/// (<paramref name="Objects"/>) directly under a prefix, plus a <paramref name="NextContinuationToken"/>
/// to fetch the following page (null when the listing is complete).
/// </summary>
public sealed record R2Listing(
    IReadOnlyList<string> Prefixes,
    IReadOnlyList<R2Object> Objects,
    string? NextContinuationToken)
{
    public static R2Listing Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<R2Object>(), null);
}

/// <summary>Small text/JSON object content pulled inline for preview, flagged when it was truncated.</summary>
public sealed record R2ObjectText(string Text, string ContentType, bool Truncated);

/// <summary>
/// Live reachability of one bucket, for the admin diagnostics strip. <paramref name="Reachable"/> is true when
/// a list call returned without error (the bucket exists and the credentials can read it); <paramref name="Error"/>
/// carries the failure message otherwise (e.g. <c>NoSuchBucket</c> when the bucket was never created in Cloudflare).
/// <paramref name="HasObjects"/> distinguishes "reachable but empty" (nothing has been written yet) from
/// "reachable with data". This is what makes an otherwise-silent misconfiguration — uploads failing because the
/// per-concern bucket doesn't exist — visible without trawling container logs.
/// </summary>
public sealed record R2BucketHealth(
    R2Bucket Bucket,
    string BucketName,
    bool Reachable,
    bool HasObjects,
    string? PublicUrl,
    string? Error);

public interface IR2StorageService
{
    /// <summary>True when R2 credentials are present and uploads will be attempted.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Probes one bucket with a single-key list to report whether it is reachable (exists + credentials can
    /// read it) and whether it holds any objects. Surfaces the underlying error instead of swallowing it, so a
    /// missing/misnamed bucket that silently fails every write shows up in the admin diagnostics. Never throws.
    /// </summary>
    Task<R2BucketHealth> ProbeBucketAsync(R2Bucket bucket, CancellationToken ct = default);

    /// <summary>
    /// Downloads <paramref name="sourceUrl"/> and uploads it to the <see cref="R2Bucket.Images"/> bucket
    /// under <paramref name="keyPrefix"/>, returning the hosted URL. Returns the original
    /// <paramref name="sourceUrl"/> unchanged when R2 is not configured, the URL is empty/already R2-hosted,
    /// or anything goes wrong — never throws.
    /// </summary>
    Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default);

    /// <summary>
    /// Uploads a JSON document to the given <paramref name="bucket"/> at the human-readable object key
    /// <paramref name="objectKey"/> (e.g. <c>samsung/galaxy-s24/brand-site.json</c>) and returns the hosted
    /// URL. Used to persist raw scraped specs and the final canonical spec sheet (<see cref="R2Bucket.Specs"/>)
    /// or raw scraped-data dumps (<see cref="R2Bucket.Data"/>) outside the database. Returns null when R2 is
    /// not configured, the bucket has no public host, the input is empty, or anything goes wrong — never throws.
    /// </summary>
    Task<string?> StoreJsonAsync(
        string json, string objectKey, R2Bucket bucket = R2Bucket.Specs, CancellationToken ct = default);

    /// <summary>
    /// Lists <paramref name="bucket"/> one "folder" deep under <paramref name="prefix"/> (delimiter <c>/</c>):
    /// immediate sub-prefixes and the objects directly beneath it. Pass <paramref name="continuationToken"/>
    /// from a previous page to continue. Returns <see cref="R2Listing.Empty"/> when unconfigured or on error.
    /// </summary>
    Task<R2Listing> ListObjectsAsync(
        string? prefix, string? continuationToken = null, int maxKeys = 200,
        R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default);

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> of a text/JSON object from <paramref name="bucket"/> for inline
    /// preview. Returns null when unconfigured, the object is missing, or it's larger-than-cap binary; sets
    /// Truncated when clipped.
    /// </summary>
    Task<R2ObjectText?> ReadTextAsync(
        string key, long maxBytes = 256 * 1024, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default);

    /// <summary>
    /// A time-limited presigned GET URL for <paramref name="key"/> in <paramref name="bucket"/>, usable as an
    /// <c>&lt;img&gt;</c> src or a download link without the bucket being public. Returns null when unconfigured.
    /// </summary>
    string? DownloadUrl(string key, R2Bucket bucket = R2Bucket.Data, TimeSpan? expiry = null);

    /// <summary>
    /// Uploads a complete local log day file to the <see cref="R2Bucket.Logs"/> bucket with an HONEST
    /// success signal — unlike <see cref="StoreJsonAsync"/>, whose null return conflates "failed" with
    /// "stored on a private bucket" and whose 4MB cap a day of logs outgrows. Used only by the
    /// <see cref="Daleel.Web.Logging.LogFileMirror"/>; the default is a no-op so existing fakes and the
    /// null store need no change.
    /// </summary>
    Task<bool> StoreLogFileAsync(string content, string objectKey, CancellationToken ct = default) =>
        Task.FromResult(false);
}

/// <summary>No-op storage used when R2 is unconfigured: keeps the original URL so images still render.</summary>
public sealed class NullR2StorageService : IR2StorageService
{
    public bool IsConfigured => false;

    // R2 is not configured at all: report every bucket as unreachable with a clear reason rather than an error.
    public Task<R2BucketHealth> ProbeBucketAsync(R2Bucket bucket, CancellationToken ct = default) =>
        Task.FromResult(new R2BucketHealth(bucket, "—", Reachable: false, HasObjects: false, PublicUrl: null,
            Error: "R2 is not configured"));

    public Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default) =>
        Task.FromResult(sourceUrl);

    // No bucket to write to and no public host to serve from, so there is no URL to return.
    public Task<string?> StoreJsonAsync(
        string json, string objectKey, R2Bucket bucket = R2Bucket.Specs, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<R2Listing> ListObjectsAsync(
        string? prefix, string? continuationToken = null, int maxKeys = 200,
        R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) =>
        Task.FromResult(R2Listing.Empty);

    public Task<R2ObjectText?> ReadTextAsync(
        string key, long maxBytes = 256 * 1024, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default) =>
        Task.FromResult<R2ObjectText?>(null);

    public string? DownloadUrl(string key, R2Bucket bucket = R2Bucket.Data, TimeSpan? expiry = null) => null;
}

public sealed class R2StorageService : IR2StorageService, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly HttpClient _http;
    private readonly R2Options _options;
    private readonly ILogger<R2StorageService> _logger;

    /// <summary>Cap on a single image we'll pull into memory before uploading (8 MB covers product shots).</summary>
    private const long MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>Cap on a spec-sheet JSON blob (4 MB is far beyond any real spec document).</summary>
    private const long MaxJsonBytes = 4 * 1024 * 1024;

    static R2StorageService()
    {
        // Cloudflare R2 only accepts SigV4. For a custom ServiceURL the SDK otherwise presigns with SigV2
        // (AWSAccessKeyId/Signature query params), which R2 rejects with 403 — so download/preview URLs
        // would silently break. This process-global toggle makes GetPreSignedURL emit SigV4 (X-Amz-*).
        Amazon.AWSConfigsS3.UseSignatureVersion4 = true;
    }

    public bool IsConfigured => true;

    public R2StorageService(R2Options options, HttpClient http, ILogger<R2StorageService> logger)
    {
        _options = options;
        _http = http;
        _logger = logger;

        var config = new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            // R2 is path-style only (bucket in the path, not a vhost subdomain).
            ForcePathStyle = true
        };
        // One S3 client serves every bucket: the connection (endpoint + credentials) is shared, only the
        // BucketName per request differs, so each call routes itself to logs/images/specs/data.
        _s3 = new AmazonS3Client(options.AccessKey, options.SecretKey, config);
    }

    /// <summary>
    /// Test/DI seam: inject a fake <see cref="IAmazonS3"/> and <see cref="HttpClient"/> plus the full
    /// per-bucket <see cref="R2Options"/> so routing can be exercised directly.
    /// </summary>
    public R2StorageService(IAmazonS3 s3, HttpClient http, R2Options options, ILogger<R2StorageService> logger)
    {
        _s3 = s3;
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <summary>The bucket name a concern routes to.</summary>
    private string BucketName(R2Bucket bucket) => _options.For(bucket).BucketName;

    /// <summary>
    /// The public host for a concern with the trailing slash trimmed (so <c>"{host}/{key}"</c> never
    /// doubles it), or empty string when the bucket is private (no browser-loadable URL can be minted).
    /// </summary>
    private string PublicBaseUrl(R2Bucket bucket) => _options.For(bucket).PublicUrl?.TrimEnd('/') ?? string.Empty;

    public async Task<string?> StoreImageAsync(string? sourceUrl, string keyPrefix, CancellationToken ct = default)
    {
        // Images always live in the dedicated images bucket, served from its public host.
        var publicBaseUrl = PublicBaseUrl(R2Bucket.Images);

        if (string.IsNullOrWhiteSpace(sourceUrl) ||
            !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return sourceUrl;
        }

        // No public host configured (R2_PUBLIC_URL_IMAGES unset): we cannot mint a browser-loadable URL, so
        // don't rewrite. Hot-link the original instead — uploading and returning "{serviceUrl}/{bucket}/{key}"
        // would point an <img> at the S3 API endpoint, which 403s every unauthenticated GET, so every
        // hosted image would silently break. Hot-linking the source is the correct graceful degradation.
        if (string.IsNullOrEmpty(publicBaseUrl))
        {
            return sourceUrl;
        }

        // Already hosted by us — nothing to copy, and re-uploading would loop.
        if (sourceUrl.StartsWith(publicBaseUrl, StringComparison.OrdinalIgnoreCase))
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
                BucketName = BucketName(R2Bucket.Images),
                Key = key,
                InputStream = new MemoryStream(bytes),
                ContentType = contentType,
                // R2 rejects AWS SigV4 streaming payload signing — disabling it is the documented fix for
                // S3-compatible PUTs to R2 (the same knob the Serilog AmazonS3 sink sets).
                DisablePayloadSigning = true
            }, ct).ConfigureAwait(false);

            return $"{publicBaseUrl}/{key}";
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

    public async Task<string?> StoreJsonAsync(
        string json, string objectKey, R2Bucket bucket = R2Bucket.Specs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(objectKey))
        {
            return null;
        }

        // The object is always worth storing even when the bucket has no public host: the admin data viewer
        // reads it back through a presigned GET (which works on a private bucket), so skipping the upload
        // here is what left the specs/data browser empty. We still only RETURN a hosted "{host}/{key}" URL
        // when a public host exists — callers persist that for hot-linking; a null just means "stored, but
        // not publicly hot-linkable" (the DB copy stays canonical), not "not stored".
        var publicBaseUrl = PublicBaseUrl(bucket);

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
                BucketName = BucketName(bucket),
                Key = key,
                InputStream = new MemoryStream(bytes),
                ContentType = "application/json",
                // R2 rejects AWS SigV4 streaming payload signing — same fix as the image path.
                DisablePayloadSigning = true
            }, ct).ConfigureAwait(false);

            // Hosted URL only when the bucket is public; otherwise it's stored-but-not-hot-linkable → null.
            return string.IsNullOrEmpty(publicBaseUrl) ? null : $"{publicBaseUrl}/{key}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: a failed upload must never break the spec pipeline — the DB copy remains canonical.
            // Logged at Warning (not Debug) so a genuine R2 problem — most often a per-concern bucket that was
            // never created in Cloudflare, which fails EVERY specs/data write — is visible without a live probe.
            _logger.LogWarning(ex, "R2 JSON store failed for {Bucket}/{Key}", BucketName(bucket), key);
            return null;
        }
    }

    /// <summary>
    /// Full-file log mirror upload (see the interface remarks): no public-host dependency in the return,
    /// no <see cref="MaxJsonBytes"/> cap (the mirror applies its own), newline-delimited JSON content type.
    /// </summary>
    public async Task<bool> StoreLogFileAsync(string content, string objectKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrWhiteSpace(objectKey))
        {
            return false;
        }

        var key = NormalizeObjectKey(objectKey);
        try
        {
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = BucketName(R2Bucket.Logs),
                Key = key,
                InputStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                ContentType = "application/x-ndjson",
                // R2 rejects AWS SigV4 streaming payload signing — same fix as every other upload here.
                DisablePayloadSigning = true
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "R2 log mirror upload failed for {Key}", key);
            return false;
        }
    }

    public async Task<R2BucketHealth> ProbeBucketAsync(R2Bucket bucket, CancellationToken ct = default)
    {
        var config = _options.For(bucket);
        try
        {
            // One key is enough to prove the bucket exists and is readable; KeyCount tells empty from populated.
            var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = config.BucketName,
                MaxKeys = 1
            }, ct).ConfigureAwait(false);

            var hasObjects = (response.KeyCount > 0) || (response.S3Objects is { Count: > 0 });
            return new R2BucketHealth(bucket, config.BucketName, Reachable: true, hasObjects, config.PublicUrl, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "R2 probe failed for bucket {Bucket}", config.BucketName);
            return new R2BucketHealth(bucket, config.BucketName, Reachable: false, HasObjects: false,
                config.PublicUrl, ex.Message);
        }
    }

    /// <summary>Default lifetime for a presigned download/preview URL — long enough to view, short enough to not leak.</summary>
    private static readonly TimeSpan DefaultUrlLifetime = TimeSpan.FromMinutes(15);

    public async Task<R2Listing> ListObjectsAsync(
        string? prefix, string? continuationToken = null, int maxKeys = 200,
        R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default)
    {
        try
        {
            var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = BucketName(bucket),
                // Delimiter "/" makes R2 fold deeper keys into CommonPrefixes, giving a one-level "folder"
                // view instead of every object in the bucket at once.
                Delimiter = "/",
                Prefix = string.IsNullOrEmpty(prefix) ? null : prefix,
                ContinuationToken = string.IsNullOrEmpty(continuationToken) ? null : continuationToken,
                MaxKeys = Math.Clamp(maxKeys, 1, 1000)
            }, ct).ConfigureAwait(false);

            return MapListing(response, prefix);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "R2 list failed for prefix {Prefix}", prefix);
            return R2Listing.Empty;
        }
    }

    /// <summary>
    /// Pure mapper from the S3 listing response to our <see cref="R2Listing"/>: keeps the AWS shape out of
    /// the UI and stays unit-testable. The object whose key equals the prefix itself (the "folder marker")
    /// is dropped so it doesn't show as a zero-byte file.
    /// </summary>
    internal static R2Listing MapListing(ListObjectsV2Response response, string? prefix)
    {
        var objects = (response.S3Objects ?? new List<S3Object>())
            .Where(o => o.Key != prefix)
            // S3 reports LastModified in UTC; pin the kind so the DateTimeOffset doesn't shift by the host TZ.
            .Select(o => new R2Object(
                o.Key, o.Size, new DateTimeOffset(DateTime.SpecifyKind(o.LastModified, DateTimeKind.Utc))))
            .ToList();

        var prefixes = (response.CommonPrefixes ?? new List<string>())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        return new R2Listing(prefixes, objects, response.IsTruncated ? response.NextContinuationToken : null);
    }

    public async Task<R2ObjectText?> ReadTextAsync(
        string key, long maxBytes = 256 * 1024, R2Bucket bucket = R2Bucket.Data, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = BucketName(bucket),
                Key = key
            }, ct).ConfigureAwait(false);

            await using var stream = response.ResponseStream;
            using var memory = new MemoryStream();
            // Read one byte past the cap so we can tell "exactly at cap" from "more remains".
            var buffer = new byte[8192];
            int read;
            var truncated = false;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > maxBytes)
                {
                    memory.Write(buffer, 0, (int)(maxBytes - memory.Length));
                    truncated = true;
                    break;
                }
                memory.Write(buffer, 0, read);
            }

            var text = Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
            return new R2ObjectText(text, response.Headers.ContentType ?? ContentTypeFor(key), truncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "R2 read failed for key {Key}", key);
            return null;
        }
    }

    public string? DownloadUrl(string key, R2Bucket bucket = R2Bucket.Data, TimeSpan? expiry = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        try
        {
            return _s3.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = BucketName(bucket),
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(expiry ?? DefaultUrlLifetime)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "R2 presign failed for key {Key}", key);
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
