using Daleel.Web.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Daleel.Web.Tests.Logging;

// The per-search logging scope: every ILogger line emitted inside it — by our code OR third-party
// code on the same async flow — carries SearchJobId as a structured property, so the whole flow of
// one search is greppable in Serilog's file/R2 JSON sinks ({"Properties":{"SearchJobId":46,...}}).
public class SearchLogScopeTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<object?> Scopes { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            Scopes.Add(state);
            return new Noop();
        }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f) { }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void Scope_carries_the_search_job_id_as_a_structured_property()
    {
        var logger = new CapturingLogger();

        using var _ = SearchLogScope.Begin(logger, searchJobId: 46);

        var state = logger.Scopes.Should().ContainSingle().Subject
            .Should().BeAssignableTo<IEnumerable<KeyValuePair<string, object?>>>().Subject;
        state.Should().Contain(new KeyValuePair<string, object?>("SearchJobId", 46));
    }

    [Fact]
    public void Scope_optionally_carries_the_unit_kind_for_queue_work()
    {
        var logger = new CapturingLogger();

        using var _ = SearchLogScope.Begin(logger, searchJobId: 46, unitKind: "enrich.catalog");

        var state = (IEnumerable<KeyValuePair<string, object?>>)logger.Scopes.Single();
        state.Should().Contain(new KeyValuePair<string, object?>("SearchJobId", 46));
        state.Should().Contain(new KeyValuePair<string, object?>("UnitKind", "enrich.catalog"));
    }

    [Fact]
    public void Null_logger_is_a_safe_noop()
    {
        var scope = SearchLogScope.Begin(null, searchJobId: 46);
        scope.Should().NotBeNull();
        scope.Dispose(); // must not throw
    }
}
