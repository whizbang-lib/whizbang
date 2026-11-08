// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tracing;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// PerspectiveWorker is hosted unconditionally by the core worker pipeline (turnkey), so a
/// service with NO registered perspectives must pay NOTHING for it while it has no work: no
/// startup-repair queries, no drain loop, no poll cadence, no database connections. The
/// regression this fences out: an always-running worker draining a small connection pool in a
/// perspective-less host — observed as pool exhaustion (Max Pool Size=2 fixtures), failed
/// signal-bus self-tests, and cascade timeouts. The worker is parked on its drain channel rather
/// than ended (#2142): the claim still hands such a host the collective rows it stores, and the
/// drain is what concludes them (see PerspectiveWorkerDeepPathDrainTests.UnreadableRows).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class PerspectiveWorkerNoPerspectivesParkTests {

  [Test]
  [Timeout(30000)]
  public async Task NoRegistry_ParksCleanly_NoCoordinatorCalls_StartupScanCompletesAsync(CancellationToken cancellationToken) {
    // ARRANGE — a scope provider that COUNTS IWorkCoordinator resolutions and has NO
    // IPerspectiveRunnerRegistry, mirroring a perspective-less host. Channels deliberately
    // unwired: a parked worker must return BEFORE the channel-requirement check, so their
    // absence must not throw. Resolution-counting is the strongest form of the invariant:
    // a parked worker never even asks for the coordinator, let alone opens a connection.
    var resolutions = 0;
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IWorkCoordinator>(_ => {
      Interlocked.Increment(ref resolutions);
      return new NoOpWorkCoordinator();
    });
    var sp = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider: new StubInstanceProvider(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions()),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: NullEventTypeProvider.Instance,
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: NullLogger<PerspectiveWorker>.Instance,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: new PerspectiveChannelWriter(),
      perspectiveCompletionChannel: new CapturingPerspectiveCompletionChannel(),
      failureChannel: new CapturingFailureChannel(),
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor((Options.Create(new PerspectiveWorkerOptions())).Value));

    // ACT — the barrier is released immediately before the park, and stopping is the only way out of it.
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    await worker.StartAsync(cts.Token);
    await worker.StartupScanComplete.WaitAsync(cancellationToken);
    var parkedBeforeStop = worker.ExecuteTask!.IsCompleted;
    await worker.StopAsync(CancellationToken.None);
    await worker.ExecuteTask.WaitAsync(cancellationToken);

    // ASSERT — parked: no loop ran and the coordinator was never touched; the stop ended it cleanly.
    await Assert.That(parkedBeforeStop).IsFalse()
      .Because("a perspective-less host is parked on its drain channel, waiting for work its claim hands it");
    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue()
      .Because("stopping a parked worker ends it cleanly — no channel-requirement throw, no loop");
    await Assert.That(resolutions).IsEqualTo(0)
      .Because("a parked PerspectiveWorker must never resolve the coordinator — it previously " +
               "ran orphan reconcile, rewind repair, and the poll loop, draining small connection pools");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
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
