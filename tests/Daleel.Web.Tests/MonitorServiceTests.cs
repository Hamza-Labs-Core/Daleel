using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

public class MonitorServiceTests
{
    private readonly MonitorService _service = new(new AgentFactory());

    [Fact]
    public void Add_CreatesAnActiveMonitor()
    {
        var monitor = _service.Add("مكيف", "jordan", 60);

        monitor.Status.Should().Be(MonitorStatus.Active);
        monitor.Keyword.Should().Be("مكيف");
        _service.Monitors.Should().ContainSingle(m => m.Id == monitor.Id);
    }

    [Fact]
    public void Toggle_FlipsBetweenActiveAndPaused()
    {
        var m = _service.Add("kw", "usa", 30);

        _service.Toggle(m.Id);
        _service.Monitors.Single(x => x.Id == m.Id).Status.Should().Be(MonitorStatus.Paused);

        _service.Toggle(m.Id);
        _service.Monitors.Single(x => x.Id == m.Id).Status.Should().Be(MonitorStatus.Active);
    }

    [Fact]
    public void Remove_DeletesTheMonitor()
    {
        var m = _service.Add("kw", "uae", 15);
        _service.Remove(m.Id);
        _service.Monitors.Should().NotContain(x => x.Id == m.Id);
    }

    [Fact]
    public async Task RunOnce_ReturnsZeroForUnknownMonitor()
    {
        var result = await _service.RunOnceAsync("does-not-exist", new Dictionary<string, string>());
        result.Should().Be(0);
    }

    [Fact]
    public async Task RunOnce_ReturnsMinusOneWhenNoApifyToken()
    {
        var m = _service.Add("kw", "jordan", 60);
        var original = Environment.GetEnvironmentVariable("APIFY_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("APIFY_TOKEN", null);
            var result = await _service.RunOnceAsync(m.Id, new Dictionary<string, string>());
            result.Should().Be(-1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APIFY_TOKEN", original);
        }
    }
}
