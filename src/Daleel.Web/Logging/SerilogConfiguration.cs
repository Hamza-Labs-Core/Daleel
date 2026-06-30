using System.Diagnostics;
using Daleel.Web.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace Daleel.Web.Logging;

/// <summary>
/// Wires Serilog as the application's logging provider, replacing the default
/// <c>Microsoft.Extensions.Logging</c> console provider.
///
/// Sink topology (all sinks are additive — none is mutually exclusive with another):
///  • Console — always on, human-readable, level-gated to the host minimum (Information in prod,
///    Debug in dev). Goes to container stdout, captured by Docker's json-file driver.
///  • File — always on. The FULL operational trail at the host minimum level, written as
///    newline-delimited JSON to the persisted <c>daleel_data</c> volume (<c>/app/data/logs</c>). This is
///    the "logs folder" operators look in; it survives container restarts.
///  • R2 (optional) — the FULL operational trail at the host minimum level (Information in prod,
///    Debug in dev), mirrored to the Cloudflare <see cref="R2Bucket.Logs"/> bucket as JSON Lines under a
///    <c>logs/</c> prefix when the <c>R2_*</c> env vars are set. This is the source the log-viewer Worker
///    reads — so it gets the same complete picture as the local file sink, not just errors.
///
/// Every event is enriched with a <c>TraceId</c> (W3C trace id from the ambient <see cref="Activity"/>)
/// so all log lines emitted while handling one request share a correlation id, plus the source context
/// (the <c>ILogger&lt;T&gt;</c> category) and full exception stack traces.
/// </summary>
public static class SerilogConfiguration
{
    /// <summary>
    /// Default on-disk location for the file sink. Relative to the content root, which is <c>/app</c> in
    /// the container (WORKDIR) — so this resolves to <c>/app/data/logs</c> on the persisted
    /// <c>daleel_data</c> volume (which also holds the Data-Protection key ring; the database itself lives
    /// in PostgreSQL). Staying relative keeps local dev writable too (an absolute <c>/app/...</c> path
    /// would fail outside Docker).
    /// </summary>
    public const string DefaultFileLogDirectory = "data/logs";

    /// <summary>
    /// Human-readable console line: ISO-ish timestamp, 3-letter level, source context, correlation id,
    /// the rendered message, then the full exception (stack trace included) on its own lines.
    /// </summary>
    private const string ConsoleOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [{SourceContext}] (trace:{TraceId}) {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Replaces the host's default logging with Serilog. Call once on the builder before
    /// <c>builder.Build()</c>. The logger is rebuilt with full DI/configuration access at host start.
    /// </summary>
    public static WebApplicationBuilder AddDaleelLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, _, loggerConfiguration) =>
            Configure(loggerConfiguration, context.Configuration, context.HostingEnvironment.IsDevelopment()));

        return builder;
    }

    /// <summary>
    /// Builds the sink topology onto <paramref name="loggerConfiguration"/>. Separated from
    /// <see cref="AddDaleelLogging"/> so the routing decision can be exercised directly.
    /// </summary>
    /// <param name="isDevelopment">
    /// When true the minimum level drops to Debug for richer local diagnostics; otherwise Information.
    /// </param>
    public static void Configure(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        // (2) Level policy: Debug in dev, Information in prod.
        var minimumLevel = isDevelopment ? LogEventLevel.Debug : LogEventLevel.Information;

        loggerConfiguration
            .MinimumLevel.Is(minimumLevel)
            // Tame framework chatter so the logs stay signal-rich.
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            // (3) Structured-logging enrichment: scoped properties, a per-request correlation id, and a
            // constant app tag so multi-service log aggregation can filter on it.
            .Enrich.FromLogContext()
            .Enrich.With<TraceIdEnricher>()
            .Enrich.WithProperty("Application", "Daleel.Web")
            // Console stays for debugging — host-minimum level, human-readable, to container stdout.
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate);

        // (1) File sink — ALWAYS on. The full trail at the host minimum level, on the persisted volume.
        ConfigureFileSink(loggerConfiguration, configuration, minimumLevel);

        // (4) R2 sink — additional, full trail at the host minimum level, when configured. Mirrors the
        // file sink off-box so the log-viewer Worker can read it.
        var r2 = R2Options.FromConfiguration(configuration);
        if (r2 is not null)
        {
            ConfigureR2Sink(loggerConfiguration, r2, minimumLevel);
        }
    }

    /// <summary>
    /// The always-on local sink: every event at <paramref name="minimumLevel"/> and above, as daily-rolling
    /// JSON Lines on the persisted data volume. <see cref="JsonFormatter"/> serializes every enriched
    /// property (SourceContext, TraceId, Application, …) and the full exception — stack trace included.
    /// </summary>
    private static void ConfigureFileSink(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        LogEventLevel minimumLevel)
    {
        var directory = configuration["FileLogging:Directory"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = DefaultFileLogDirectory;
        }

        Directory.CreateDirectory(directory);

        loggerConfiguration.WriteTo.File(
            formatter: new JsonFormatter(renderMessage: true),
            // The sink inserts the date before the extension → daleel-20260628.jsonl, one per day.
            path: Path.Combine(directory, "daleel-.jsonl"),
            restrictedToMinimumLevel: minimumLevel,
            rollingInterval: RollingInterval.Day,
            // Cap disk usage on the volume — keep two weeks of daily files.
            retainedFileCountLimit: 14,
            // Flush to disk promptly so a crash doesn't swallow the last few seconds of logs.
            flushToDiskInterval: TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// The full operational trail (at <paramref name="minimumLevel"/> and above — Information in prod,
    /// Debug in dev) mirrored to the R2 <see cref="R2Bucket.Logs"/> bucket as JSON Lines under a
    /// <c>logs/</c> prefix, one object per day: <c>logs/daleel-20260630.jsonl</c>. This is exactly the
    /// prefix the log-viewer Worker scans, and the date in the filename lets it window by day. The
    /// scraped-data objects (site-data/, final-specs/, …) keep their own prefixes and are untouched.
    /// </summary>
    private static void ConfigureR2Sink(
        LoggerConfiguration loggerConfiguration, R2Options r2, LogEventLevel minimumLevel)
    {
        // The date is fixed at process start and baked into the object key. Daleel redeploys (and thus
        // restarts) frequently, so the filename tracks the current day in practice; a process that ran
        // uninterrupted past midnight would keep appending to its start-day object. RollingInterval.Infinite
        // keeps the key exactly as given (no extra date suffix) since the date already lives in the name.
        var objectName = $"daleel-{DateTime.UtcNow:yyyyMMdd}.jsonl";

        loggerConfiguration.WriteTo.AmazonS3(
            path: objectName,
            bucketName: r2.Logs.BucketName,
            serviceUrl: r2.ServiceUrl,
            awsAccessKeyId: r2.AccessKey,
            awsSecretAccessKey: r2.SecretKey,
            // JsonFormatter writes one JSON object per line — that is the .jsonl format.
            formatter: new JsonFormatter(renderMessage: true),
            // Information+ in prod (Debug in dev): the whole picture, matching the file sink — not errors-only.
            restrictedToMinimumLevel: minimumLevel,
            // The AmazonS3 sink defines its own RollingInterval enum, distinct from the File sink's.
            rollingInterval: Serilog.Sinks.AmazonS3.RollingInterval.Infinite,
            // Group every app-log object under a single prefix so the Worker can list/search just `logs/`
            // and never trips over the scraped-data objects sharing this bucket.
            bucketPath: "logs",
            // Cloudflare R2 does not support AWS SigV4 *streaming* payload signing; disabling it is
            // the documented fix for S3-compatible uploads to R2.
            disablePayloadSigning: true,
            // Don't wait for the batch timer to flush the very first error of a run.
            eagerlyEmitFirstEvent: true);
    }

    /// <summary>
    /// Stamps each event with a <c>TraceId</c> correlation id taken from the ambient
    /// <see cref="Activity"/>. ASP.NET Core starts an <see cref="Activity"/> per request, so every log
    /// line emitted while serving one request shares the same id — making a request's logs greppable
    /// across console, file and R2. Absent an activity (e.g. startup) the property is simply omitted.
    /// </summary>
    private sealed class TraceIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var traceId = Activity.Current?.TraceId;
            if (traceId is { } id && id != default)
            {
                logEvent.AddPropertyIfAbsent(
                    propertyFactory.CreateProperty("TraceId", id.ToString()));
            }
        }
    }
}
