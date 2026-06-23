using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

public class MonitorServiceTests
{
    private const string Alice = "alice-id";
    private const string Bob = "bob-id";

    private readonly MonitorService _service = new(new AgentFactory());

    [Fact]
    public void Add_CreatesAnActiveMonitor()
    {
        var monitor = _service.Add(Alice, "مكيف", "jordan", 60);

        monitor.Status.Should().Be(MonitorStatus.Active);
        monitor.Keyword.Should().Be("مكيف");
        monitor.UserId.Should().Be(Alice);
        _service.MonitorsFor(Alice).Should().ContainSingle(m => m.Id == monitor.Id);
    }

    [Fact]
    public void Toggle_FlipsBetweenActiveAndPaused()
    {
        var m = _service.Add(Alice, "kw", "usa", 30);

        _service.Toggle(Alice, m.Id);
        _service.MonitorsFor(Alice).Single(x => x.Id == m.Id).Status.Should().Be(MonitorStatus.Paused);

        _service.Toggle(Alice, m.Id);
        _service.MonitorsFor(Alice).Single(x => x.Id == m.Id).Status.Should().Be(MonitorStatus.Active);
    }

    [Fact]
    public void Remove_DeletesTheMonitor()
    {
        var m = _service.Add(Alice, "kw", "uae", 15);
        _service.Remove(Alice, m.Id);
        _service.MonitorsFor(Alice).Should().NotContain(x => x.Id == m.Id);
    }

    [Fact]
    public async Task RunOnce_ReturnsZeroForUnknownMonitor()
    {
        var result = await _service.RunOnceAsync(Alice, "does-not-exist", new Dictionary<string, string>());
        result.Should().Be(0);
    }

    [Fact]
    public async Task RunOnce_ReturnsMinusOneWhenNoApifyToken()
    {
        var m = _service.Add(Alice, "kw", "jordan", 60);
        var original = Environment.GetEnvironmentVariable("APIFY_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("APIFY_TOKEN", null);
            var result = await _service.RunOnceAsync(Alice, m.Id, new Dictionary<string, string>());
            result.Should().Be(-1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APIFY_TOKEN", original);
        }
    }

    // ── Isolation: one user can never see, mutate, or run another's monitors ──────────────

    [Fact]
    public void MonitorsFor_ReturnsOnlyTheOwnersRows()
    {
        _service.Add(Alice, "alice-kw", "jordan", 60);
        _service.Add(Bob, "bob-kw", "jordan", 60);

        _service.MonitorsFor(Alice).Should().ContainSingle().Which.Keyword.Should().Be("alice-kw");
        _service.MonitorsFor(Bob).Should().ContainSingle().Which.Keyword.Should().Be("bob-kw");
    }

    [Fact]
    public void Remove_CannotDeleteAnotherUsersMonitor()
    {
        var aliceMonitor = _service.Add(Alice, "keep-me", "jordan", 60);

        // Bob attempts to remove Alice's monitor by id — must be a no-op.
        _service.Remove(Bob, aliceMonitor.Id);

        _service.MonitorsFor(Alice).Should().ContainSingle(m => m.Id == aliceMonitor.Id);
    }

    [Fact]
    public void Toggle_CannotMutateAnotherUsersMonitor()
    {
        var aliceMonitor = _service.Add(Alice, "kw", "jordan", 60);

        _service.Toggle(Bob, aliceMonitor.Id);

        // Alice's monitor is untouched (still Active).
        _service.MonitorsFor(Alice).Single().Status.Should().Be(MonitorStatus.Active);
    }

    [Fact]
    public async Task RunOnce_CannotRunAnotherUsersMonitor()
    {
        var aliceMonitor = _service.Add(Alice, "kw", "jordan", 60);

        // Bob runs Alice's monitor id — treated as unknown (0), so no Apify call is made on her behalf.
        var result = await _service.RunOnceAsync(Bob, aliceMonitor.Id, new Dictionary<string, string>());

        result.Should().Be(0);
    }
}
