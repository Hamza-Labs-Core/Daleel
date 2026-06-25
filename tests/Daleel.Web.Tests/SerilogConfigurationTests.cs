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
    public void FileFallback_WritesWarningAndAbove_AsJsonLines()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileLogging:Directory"] = _logDir })
            .Build();

        var loggerConfiguration = new LoggerConfiguration();
        SerilogConfiguration.Configure(loggerConfiguration, config);

        using (var logger = loggerConfiguration.CreateLogger())
        {
            logger.Information("this is below the threshold and should be dropped from the file");
            logger.Warning("disk space low on {Volume}", "daleel_data");
            logger.Error("downstream call failed");
        } // dispose flushes the file sink

        var file = Directory.GetFiles(_logDir, "errors-*.jsonl").Should().ContainSingle().Subject;
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();

        lines.Should().HaveCount(2, "only Warning and Error clear the minimum level");

        // Each line must be a standalone JSON object (the .jsonl contract).
        var first = JsonDocument.Parse(lines[0]).RootElement;
        first.GetProperty("Level").GetString().Should().Be("Warning");
        first.GetProperty("RenderedMessage").GetString().Should().Contain("daleel_data");

        JsonDocument.Parse(lines[1]).RootElement.GetProperty("Level").GetString().Should().Be("Error");
        lines.Should().NotContain(l => l.Contains("below the threshold"));
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
