// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The proposal's headline ungated defect: <see cref="PerspectiveWorker"/> ran its startup work —
/// registry initialization, orphan-lifecycle reconcile, rewind repair — against a database that may
/// not have been migrated yet. The host now starts the worker at Accept commands (#1636); what is
/// left to the worker is to settle its public startup-scan signal when the host stops before then.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/GatedHostedService.cs</code-under-test>
[Category("Startup")]
[NotInParallel(Order = 101)]
public class PerspectiveWorkerStartupGateTests {

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>Counts scope creations — the first thing every piece of the worker's startup work
  /// does is create a scope, so a zero count means none of it has begun.</summary>
  private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory {
    private readonly IServiceScopeFactory _inner = inner;
    private readonly Lock _lock = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private int _count;

    public int Count => Volatile.Read(ref _count);
    public IServiceScope CreateScope() {
      var count = Interlocked.Increment(ref _count);
      lock (_lock) {
        foreach (var waiter in _waiters.Where(w => count > w.Count).ToList()) {
          _waiters.Remove(waiter);
          waiter.Signal.TrySetResult();
        }
      }
      return _inner.CreateScope();
    }

  }

  // StartupScanComplete is a PUBLIC task other components await to know the worker's startup pass
  // has finished. A host stopped before Accept commands never starts the worker, so the signal is
  // settled through the start-abandoned hook: left pending, a host whose migrations were
  // interrupted would leave every waiter parked on a signal that can no longer arrive.
  [Test]
  [Timeout(30000)]
  public async Task StoppedBeforeAcceptCommands_SettlesTheStartupScanSignalAsync(
      CancellationToken testToken) {
    var inner = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = new CountingScopeFactory(inner.GetRequiredService<IServiceScopeFactory>());

    var worker = new PerspectiveWorker(
      instanceProvider: new StubInstanceProvider(),
      scopeFactory: scopeFactory,
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
    var baseline = scopeFactory.Count;

    // The stage gate never reaches Accept commands: the host stops first.
    using var hosted = new Whizbang.Core.Startup.StartupStageGatedService<PerspectiveWorker>(worker,
      Whizbang.Core.Startup.StartupStage.AcceptingCommands, new Whizbang.Core.Startup.StartupStageGate(new SchemaReadyGate()));
    await hosted.StartAsync(testToken);
    await hosted.StopAsync(CancellationToken.None);

    // The load-bearing assertion: the public signal is settled, so a waiter is released.
    await worker.StartupScanComplete
      .WaitAsync(testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.StartupScanComplete.IsCompleted).IsTrue()
      .Because("StartupScanComplete is public and awaited by other components — leaving it pending "
             + "after the worker has given up turns an interrupted migration into a permanent hang");
    await Assert.That(worker.StartupScanComplete.IsCanceled).IsTrue()
      .Because("the scan did not run, so the signal must report cancellation rather than success — "
             + "completing it normally would tell a waiter a startup pass happened that did not");
    await Assert.That(scopeFactory.Count).IsEqualTo(baseline)
      .Because("the worker never started, so none of the startup work touched the database");
  }
}
