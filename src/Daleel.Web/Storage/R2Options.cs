namespace Daleel.Web.Storage;

/// <summary>
/// The four storage concerns, each backed by its own Cloudflare R2 bucket so logs, images, specs and
/// scraped data never share a bucket. The <see cref="R2StorageService"/> routes every write/read to the
/// bucket named here.
/// </summary>
public enum R2Bucket
{
    /// <summary>Application error logs (Serilog JSON Lines). Never served publicly.</summary>
    Logs,

    /// <summary>Product images, brand logos, store photos. Served publicly via <see cref="R2BucketConfig.PublicUrl"/>.</summary>
    Images,

    /// <summary>Raw and final product-spec JSON files.</summary>
    Specs,

    /// <summary>Site-data, brand catalogs, scraped data dumps.</summary>
    Data
}

/// <summary>One bucket's name plus the optional public host used to mint browser-loadable URLs for it.</summary>
/// <param name="BucketName">Target R2 bucket — must already exist; the app never creates it.</param>
/// <param name="PublicUrl">
/// Public host (bucket r2.dev URL or a custom domain bound to the bucket) used to build
/// <c>{PublicUrl}/{key}</c>. Null when the bucket is private — callers degrade gracefully (images
/// hot-link the source, JSON stores return no hosted URL). Logs never need one.
/// </param>
public sealed record R2BucketConfig(string BucketName, string? PublicUrl);

/// <summary>
/// Full Cloudflare R2 (S3-compatible) configuration: one shared connection (credentials + endpoint) and a
/// separate <see cref="R2BucketConfig"/> per concern. Read from the <c>R2_*</c> environment / configuration
/// keys. A non-null instance proves the connection is fully set; bucket names always resolve because they
/// default, so callers branch on null only for "R2 is not configured at all".
/// </summary>
public sealed record R2Options(
    string AccessKey,
    string SecretKey,
    string ServiceUrl,
    R2BucketConfig Logs,
    R2BucketConfig Images,
    R2BucketConfig Specs,
    R2BucketConfig Data)
{
    /// <summary>Resolves the bucket config for a concern.</summary>
    public R2BucketConfig For(R2Bucket bucket) => bucket switch
    {
        R2Bucket.Logs => Logs,
        R2Bucket.Images => Images,
        R2Bucket.Specs => Specs,
        R2Bucket.Data => Data,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "Unknown R2 bucket")
    };

    /// <summary>
    /// Builds the options from configuration, or returns <c>null</c> when R2 is not configured. The gate is
    /// the shared connection (access key + secret + a resolvable endpoint); bucket names are NOT required
    /// because each defaults to a conventional <c>daleel-*</c> name — the operator just creates the buckets
    /// in Cloudflare with those names. Legacy single-bucket vars (<c>R2_BUCKET_NAME</c>, <c>R2_PUBLIC_URL</c>)
    /// are honoured as fallbacks so existing deployments keep working.
    /// </summary>
    public static R2Options? FromConfiguration(IConfiguration config)
    {
        var accessKey = RealValue(config["R2_ACCESS_KEY"]);
        var secretKey = RealValue(config["R2_SECRET_KEY"]);
        // R2 lives under the same Cloudflare account as the rest of the app, so we reuse the existing
        // CLOUDFLARE_ACCOUNT_ID rather than asking operators to set a redundant R2-specific account id.
        var accountId = RealValue(config["CLOUDFLARE_ACCOUNT_ID"]);
        var endpoint = RealValue(config["R2_ENDPOINT"]);

        // Prefer an explicit endpoint; otherwise derive the canonical R2 S3 URL from the account id.
        var serviceUrl = endpoint
            ?? (accountId is not null ? $"https://{accountId}.r2.cloudflarestorage.com" : null);

        // Credentials + a resolvable endpoint are mandatory; anything short of that means "not configured".
        if (accessKey is null || secretKey is null || serviceUrl is null)
        {
            return null;
        }

        // Backward-compat: the old single-bucket setup used R2_BUCKET_NAME (which held the logs bucket) and
        // R2_PUBLIC_URL (the image host). Honour them as fallbacks for those two concerns.
        var legacyBucket = RealValue(config["R2_BUCKET_NAME"]);
        var legacyPublicUrl = RealValue(config["R2_PUBLIC_URL"]);

        var logs = new R2BucketConfig(
            RealValue(config["R2_BUCKET_LOGS"]) ?? legacyBucket ?? "daleel-logs",
            PublicUrl: null); // logs are never served publicly — only written and read back by admins via presign
        var images = new R2BucketConfig(
            RealValue(config["R2_BUCKET_IMAGES"]) ?? "daleel-images",
            RealValue(config["R2_PUBLIC_URL_IMAGES"]) ?? legacyPublicUrl);
        var specs = new R2BucketConfig(
            RealValue(config["R2_BUCKET_SPECS"]) ?? "daleel-specs",
            RealValue(config["R2_PUBLIC_URL_SPECS"]));
        var data = new R2BucketConfig(
            RealValue(config["R2_BUCKET_DATA"]) ?? "daleel-data",
            RealValue(config["R2_PUBLIC_URL_DATA"]));

        return new R2Options(accessKey, secretKey, serviceUrl, logs, images, specs, data);
    }

    /// <summary>
    /// Normalizes a raw config value to "real, or absent". Returns the trimmed value when it is genuinely
    /// set, or <c>null</c> for blank/whitespace OR the <c>CHANGE_ME</c> placeholder that
    /// <c>deploy/create-secrets.sh</c> seeds — so a seeded-but-unfilled secret reads as "not configured"
    /// rather than dialling R2 with bogus credentials.
    /// </summary>
    private static string? RealValue(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "CHANGE_ME", StringComparison.Ordinal)
            ? null
            : trimmed;
    }
}
