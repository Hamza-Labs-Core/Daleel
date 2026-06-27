using Daleel.Web.Storage;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace Daleel.Web.Logging;

/// <summary>
/// Wires Serilog as the application's logging provider, replacing the default
/// <c>Microsoft.Extensions.Logging</c> console provider.
///
/// Sinks:
///  • Console — always on, full Information+ output for local debugging / container stdout.
///  • Warning-level and above as newline-delimited JSON, sent to Cloudflare R2 when configured
///    (<c>R2_*</c> env vars), otherwise to local files under <c>/app/data/logs</c>.
/// </summary>
public static class SerilogConfiguration
{
    /// <summary>
    /// Default on-disk location for the file fallback. Relative to the content root, which is
    /// <c>/app</c> in the container (WORKDIR) — so this resolves to <c>/app/data/logs</c> on the
    /// persisted <c>daleel_data</c> volume, exactly like the SQLite DB (<c>data/daleel.db</c>). Staying
    /// relative keeps local dev writable too (an absolute <c>/app/...</c> path would fail outside Docker).
    /// </summary>
    public const string DefaultFileLogDirectory = "data/logs";

    /// <summary>
    /// Replaces the host's default logging with Serilog. Call once on the builder before
    /// <c>builder.Build()</c>. The logger is rebuilt with full DI/configuration access at host start.
    /// </summary>
    public static WebApplicationBuilder AddDaleelLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, _, loggerConfiguration) =>
            Configure(loggerConfiguration, context.Configuration));

        return builder;
    }

    /// <summary>
    /// Builds the sink topology onto <paramref name="loggerConfiguration"/>. Separated from
    /// <see cref="AddDaleelLogging"/> so the routing decision can be exercised directly.
    /// </summary>
    public static void Configure(LoggerConfiguration loggerConfiguration, IConfiguration configuration)
    {
        loggerConfiguration
            .MinimumLevel.Information()
            // Tame framework chatter so the error logs stay signal-rich.
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            // (4) Console stays for debug — Information+, human-readable, to container stdout.
            .WriteTo.Console();

        var r2 = R2Options.FromConfiguration(configuration);
        if (r2 is not null)
        {
            ConfigureR2Sink(loggerConfiguration, r2);
        }
        else
        {
            ConfigureFileFallback(loggerConfiguration, configuration);
        }
    }

    /// <summary>
    /// (3) Warning-level and above to the R2 <see cref="R2Bucket.Logs"/> bucket as JSON Lines, partitioned
    /// into a date folder so a day's errors live together: <c>errors/2026/06/24/errors.jsonl</c>.
    /// </summary>
    private static void ConfigureR2Sink(LoggerConfiguration loggerConfiguration, R2Options r2)
    {
        // The date folder is fixed at process start. Daleel redeploys (and thus restarts) frequently,
        // so the folder tracks the current day in practice; a process that ran uninterrupted past
        // midnight would keep appending to its start-day folder. RollingInterval.Infinite keeps the
        // key exactly "errors.jsonl" (no date suffix) since the date already lives in the folder.
        var bucketPath = $"errors/{DateTime.UtcNow:yyyy/MM/dd}";

        loggerConfiguration.WriteTo.AmazonS3(
            path: "errors.jsonl",
            bucketName: r2.Logs.BucketName,
            serviceUrl: r2.ServiceUrl,
            awsAccessKeyId: r2.AccessKey,
            awsSecretAccessKey: r2.SecretKey,
            // JsonFormatter writes one JSON object per line — that is the .jsonl format.
            formatter: new JsonFormatter(renderMessage: true),
            restrictedToMinimumLevel: LogEventLevel.Warning,
            // The AmazonS3 sink defines its own RollingInterval enum, distinct from the File sink's.
            rollingInterval: Serilog.Sinks.AmazonS3.RollingInterval.Infinite,
            bucketPath: bucketPath,
            // Cloudflare R2 does not support AWS SigV4 *streaming* payload signing; disabling it is
            // the documented fix for S3-compatible uploads to R2.
            disablePayloadSigning: true,
            // Don't wait for the batch timer to flush the very first error of a run.
            eagerlyEmitFirstEvent: true);
    }

    /// <summary>
    /// (8) Graceful fallback when R2 is not configured: Warning-level and above to daily-rolling JSON Lines
    /// files on the persisted data volume.
    /// </summary>
    private static void ConfigureFileFallback(LoggerConfiguration loggerConfiguration, IConfiguration configuration)
    {
        var directory = configuration["FileLogging:Directory"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = DefaultFileLogDirectory;
        }

        Directory.CreateDirectory(directory);

        loggerConfiguration.WriteTo.File(
            formatter: new JsonFormatter(renderMessage: true),
            // The sink inserts the date before the extension → errors-20260624.jsonl, one per day.
            path: Path.Combine(directory, "errors-.jsonl"),
            restrictedToMinimumLevel: LogEventLevel.Warning,
            rollingInterval: RollingInterval.Day,
            // Cap disk usage on the volume — keep two weeks of daily error files.
            retainedFileCountLimit: 14);
    }
}
