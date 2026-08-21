using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// PerspectiveWorker is hosted unconditionally by the core worker pipeline (turnkey), so a
/// service with NO registered perspectives must pay NOTHING for it: no startup-repair queries,
/// no drain loop, no poll cadence, no database connections. Before the core registration such
/// services had no PerspectiveWorker at all; the park restores exactly that footprint. The
/// regression this fences out: an always-running worker draining a small connection pool in a
/// perspective-less host — observed as pool exhaustion (Max Pool Size=2 fixtures), failed
/// signal-bus self-tests, and cascade timeouts.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class PerspectiveWorkerNoPerspectivesParkTests {

  [Test]
  public async Task NoRegistry_ParksCleanly_NoCoordinatorCalls_StartupScanCompletesAsync() {
    // ARRANGE — a scope provider that COUNTS IWorkCoordinator resolutions and has NO
    // IPerspectiveRunnerRegistry, mirroring a perspective-less host. Channels deliberately
    // unwired: a parked worker must return BEFORE the channel-requirement check, so their
    // absence must not throw. Resolution-counting is the strongest form of the invariant:
    // a parked worker never even asks for the coordinator, let alone opens a connection.
    var resolutions = 0;
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => {
      Interlocked.Increment(ref resolutions);
      return new NoOpWorkCoordinator();
    });
    var sp = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      new StubInstanceProvider(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions()));

    // ACT
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await worker.StartAsync(cts.Token);
    await worker.StartupScanComplete.WaitAsync(TimeSpan.FromSeconds(10));
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

    // ASSERT — parked: the execute loop ended cleanly without ever touching the coordinator.
    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue()
      .Because("a perspective-less host parks the worker cleanly — no channel-requirement throw, no loop");
    await Assert.That(resolutions).IsEqualTo(0)
      .Because("a parked PerspectiveWorker must never resolve the coordinator — it previously " +
               "ran orphan reconcile, rewind repair, and the poll loop, draining small connection pools");

    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    public string ServiceName => "no-perspectives-host";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
