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
///  • R2 (optional) — Warning+ only, mirrored to the Cloudflare <see cref="R2Bucket.Logs"/> bucket as
///    JSON Lines when the <c>R2_*</c> env vars are set. Errors-only to keep object-storage cost/volume
///    down; the full trail still lives on the file sink.
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

        // (4) R2 sink — additional, Warning+ only, when configured. The full trail still lives on disk.
        var r2 = R2Options.FromConfiguration(configuration);
        if (r2 is not null)
        {
            ConfigureR2Sink(loggerConfiguration, r2);
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
    /// Warning-level and above mirrored to the R2 <see cref="R2Bucket.Logs"/> bucket as JSON Lines,
    /// partitioned into a date folder so a day's errors live together: <c>errors/2026/06/24/errors.jsonl</c>.
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
