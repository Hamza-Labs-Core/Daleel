namespace Daleel.Web.Logging;

/// <summary>
/// Strongly-typed Cloudflare R2 (S3-compatible) destination for error logs, read from
/// environment / configuration. The presence of a non-null instance is itself the proof that
/// every field required to talk to R2 is set — callers branch on null to fall back to file logging,
/// and never have to re-check individual keys.
/// </summary>
/// <param name="AccessKey">R2 access key id (S3 <c>awsAccessKeyId</c>).</param>
/// <param name="SecretKey">R2 secret access key (S3 <c>awsSecretAccessKey</c>).</param>
/// <param name="BucketName">Target R2 bucket — must already exist; the sink does not create it.</param>
/// <param name="ServiceUrl">Full S3 service URL, e.g. https://&lt;account&gt;.r2.cloudflarestorage.com.</param>
public sealed record R2LoggingOptions(
    string AccessKey,
    string SecretKey,
    string BucketName,
    string ServiceUrl)
{
    /// <summary>
    /// Reads the <c>R2_*</c> keys from configuration (environment variables are folded into
    /// <see cref="IConfiguration"/> by the ASP.NET host, so no explicit env lookup is needed).
    /// Returns <c>null</c> when R2 is not fully configured, signalling the caller to fall back to
    /// file-based logging.
    /// </summary>
    public static R2LoggingOptions? FromConfiguration(IConfiguration config)
    {
        var accessKey = config["R2_ACCESS_KEY"];
        var secretKey = config["R2_SECRET_KEY"];
        var bucket = config["R2_BUCKET_NAME"];
        // R2 lives under the same Cloudflare account as the rest of the app, so we reuse the
        // existing CLOUDFLARE_ACCOUNT_ID rather than asking operators to set a redundant
        // R2-specific account id. One fewer secret to manage and keep in sync.
        var accountId = config["CLOUDFLARE_ACCOUNT_ID"];
        var endpoint = config["R2_ENDPOINT"];

        // Prefer an explicit endpoint; otherwise derive the canonical R2 S3 URL from the account id.
        // This lets operators set just CLOUDFLARE_ACCOUNT_ID and get the right host for free, while
        // still allowing a full override (e.g. a jurisdiction-specific endpoint) via R2_ENDPOINT.
        var serviceUrl = !string.IsNullOrWhiteSpace(endpoint)
            ? endpoint.Trim()
            : !string.IsNullOrWhiteSpace(accountId)
                ? $"https://{accountId.Trim()}.r2.cloudflarestorage.com"
                : null;

        // Credentials + bucket + a resolvable endpoint are all mandatory; anything short of the full
        // set means "not configured" — half-set R2 config is treated the same as none, so we never
        // hand the S3 sink a connection it cannot complete.
        if (string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey) ||
            string.IsNullOrWhiteSpace(bucket) ||
            string.IsNullOrWhiteSpace(serviceUrl))
        {
            return null;
        }

        return new R2LoggingOptions(accessKey.Trim(), secretKey.Trim(), bucket.Trim(), serviceUrl);
    }
}
