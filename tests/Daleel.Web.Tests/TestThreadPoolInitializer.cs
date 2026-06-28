using System.Runtime.CompilerServices;

namespace Daleel.Web.Tests;

/// <summary>
/// Raises the thread-pool minimum thread count before any test runs.
/// </summary>
/// <remarks>
/// The shared Postgres test container is started with a blocking sync-over-async call
/// (<c>container.StartAsync().GetAwaiter().GetResult()</c> in <see cref="Data.PostgresTestServer"/>).
/// The thread pool's <em>minimum</em> defaults to the machine's core count, which is ~2 on a CI
/// runner. When several Postgres-backed test classes run in parallel, every pool thread can block
/// inside that <c>.GetResult()</c> waiting for an async continuation that has no free thread left to
/// run on — a classic thread-pool starvation deadlock. The pool grows by only ~1 thread/sec, so
/// under sustained contention it never recovers and the whole <c>dotnet test</c> hangs (it ran for
/// 1h52m on CI before being killed). It never reproduces on a many-core dev box because the default
/// floor is already high enough. Repro locally with <c>DOTNET_PROCESSOR_COUNT=2</c>.
///
/// Raising the floor guarantees enough threads for the container-start continuations so the tests
/// proceed. This is the standard mitigation for sync-over-async starvation in a test harness.
/// </remarks>
internal static class TestThreadPoolInitializer
{
    [ModuleInitializer]
    internal static void RaiseThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out var worker, out var completionPort);
        ThreadPool.SetMinThreads(Math.Max(worker, 32), Math.Max(completionPort, 32));
    }
}
