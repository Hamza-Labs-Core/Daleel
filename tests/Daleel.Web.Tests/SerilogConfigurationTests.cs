using System.Text.Json;
using Daleel.Web.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Xunit;

namespace Daleel.Web.Tests;

public class SerilogConfigurationTests : IDisposable
{
    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "daleel-log-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FileSink_CapturesInformationAndAbove_AsJsonLines()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileLogging:Directory"] = _logDir })
            .Build();

        var loggerConfiguration = new LoggerConfiguration();
        // Production defaults: minimum level Information, so Debug drops and Information+ is kept.
        SerilogConfiguration.Configure(loggerConfiguration, config, isDevelopment: false);

        using (var logger = loggerConfiguration.CreateLogger())
        {
            logger.Debug("verbose detail dropped at the Information minimum");
            logger.Information("search completed for {Market}", "USA");
            logger.Warning("disk space low on {Volume}", "daleel_data");
            logger.Error("downstream call failed");
        } // dispose flushes the file sink

        var file = Directory.GetFiles(_logDir, "daleel-*.jsonl").Should().ContainSingle().Subject;
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();

        lines.Should().HaveCount(3, "Information, Warning and Error clear the Information minimum; Debug does not");
        lines.Should().NotContain(l => l.Contains("verbose detail dropped"));

        // Each line must be a standalone JSON object (the .jsonl contract).
        var first = JsonDocument.Parse(lines[0]).RootElement;
        first.GetProperty("Level").GetString().Should().Be("Information");
        first.GetProperty("RenderedMessage").GetString().Should().Contain("USA");
        // Structured enrichment: the constant app tag rides along on every event.
        first.GetProperty("Properties").GetProperty("Application").GetString().Should().Be("Daleel.Web");

        JsonDocument.Parse(lines[2]).RootElement.GetProperty("Level").GetString().Should().Be("Error");
    }

    [Fact]
    public void Configure_InDevelopment_CapturesDebug()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileLogging:Directory"] = _logDir })
            .Build();

        var loggerConfiguration = new LoggerConfiguration();
        SerilogConfiguration.Configure(loggerConfiguration, config, isDevelopment: true);

        using (var logger = loggerConfiguration.CreateLogger())
        {
            logger.Debug("verbose detail kept in development");
        }

        var file = Directory.GetFiles(_logDir, "daleel-*.jsonl").Should().ContainSingle().Subject;
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();

        lines.Should().ContainSingle();
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("Level").GetString().Should().Be("Debug");
    }

    [Fact]
    public void Configure_WithoutR2_DoesNotThrow_AndCreatesLogDirectory()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileLogging:Directory"] = _logDir })
            .Build();

        var act = () => SerilogConfiguration.Configure(new LoggerConfiguration(), config);

        act.Should().NotThrow();
        Directory.Exists(_logDir).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
        {
            Directory.Delete(_logDir, recursive: true);
        }
    }
}
