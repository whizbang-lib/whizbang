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
/// registry initialization, orphan-lifecycle reconcile, rewind repair — the moment
/// <c>ExecuteAsync</c> began, against a database that may not have been migrated yet, while a
/// comment in the same file claimed the schema gate "has already been awaited". On a cold database
/// that work silently no-ops inside a catch-all, and that boot's rewind repair simply does not
/// happen. These tests hold the worker to actually waiting.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
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

    /// <summary>Completes once more than <paramref name="baseline"/> scopes have been created.</summary>
    public Task CreatedMoreThan(int baseline) {
      lock (_lock) {
        if (Count > baseline) {
          return Task.CompletedTask;
        }
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((baseline, signal));
        return signal.Task;
      }
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_DoesNoStartupWorkUntilTheGateOpensAsync(CancellationToken cancellationToken) {
    var inner = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = new CountingScopeFactory(inner.GetRequiredService<IServiceScopeFactory>());
    var gate = new ObservedGate();   // NOT ready

    var worker = new PerspectiveWorker(
      instanceProvider: new StubInstanceProvider(),
      scopeFactory: scopeFactory,
      options: Options.Create(new PerspectiveWorkerOptions()),
      schemaReadyGate: gate,
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

    // The constructor creates one scope to resolve its startup-scan logger — in-memory wiring,
    // not database work. The barrier under test governs everything AFTER construction.
    var baseline = scopeFactory.Count;

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // The defect: without the gate, registry init / orphan reconcile / rewind scan begin
    // immediately, and every one of them starts by creating a scope. The worker is waiting at the gate
    // once it has entered it, and nothing it did before entering may have created one.
    await gate.Entered.WaitAsync(cancellationToken);
    await Assert.That(scopeFactory.Count).IsEqualTo(baseline)
      .Because("no startup work may touch the database before migrations have completed — "
             + "this is the ungated repair the comment wrongly claimed was gated");

    // Once the gate opens the startup work must actually run: waiting is not skipping.
    gate.MarkReady();
    await scopeFactory.CreatedMoreThan(baseline).WaitAsync(cancellationToken);
    await Assert.That(scopeFactory.Count).IsGreaterThan(baseline);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_WithNoGateSupplied_BehavesAsBeforeAsync(CancellationToken cancellationToken) {
    // Test fixtures construct the worker without a gate; they must keep compiling and running.
    var inner = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = new CountingScopeFactory(inner.GetRequiredService<IServiceScopeFactory>());

    var worker = new PerspectiveWorker(
      instanceProvider: new StubInstanceProvider(),
      scopeFactory: scopeFactory,
      options: Options.Create(new PerspectiveWorkerOptions()),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
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

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await scopeFactory.CreatedMoreThan(baseline).WaitAsync(cancellationToken);
    await Assert.That(scopeFactory.Count).IsGreaterThan(baseline);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // Target: src/Whizbang.Core/Workers/PerspectiveWorker.cs:469-470 — the schema-gate cancellation
  // arm, which cancels the startup-scan signal before returning.
  //
  // StartupScanComplete is a PUBLIC task other components await to know the worker's startup pass
  // has finished. If the worker simply returned here without settling it, that task would never
  // complete: a host whose migrations were interrupted (a stopped pod, a failed migration step)
  // would leave every waiter parked on a signal that can no longer arrive, which is a hang rather
  // than a shutdown. The assertion is therefore that the task SETTLES as canceled — not merely
  // that the worker exited.
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CanceledWhileWaitingForTheGate_SettlesTheStartupScanSignalAsync(
      CancellationToken testToken) {
    var inner = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = new CountingScopeFactory(inner.GetRequiredService<IServiceScopeFactory>());
    var gate = new ParkedGate();   // never opens, and reports when the worker begins waiting

    var worker = new PerspectiveWorker(
      instanceProvider: new StubInstanceProvider(),
      scopeFactory: scopeFactory,
      options: Options.Create(new PerspectiveWorkerOptions()),
      schemaReadyGate: gate,
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

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Without this the cancellation could beat the worker to the gate, and everything below would
    // be answered by a worker that never waited.
    await gate.Entered.WaitAsync(testToken);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

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
      .Because("the worker returned at the gate, so none of the startup work touched the database");
  }

  /// <summary>A real schema gate that announces when a worker starts waiting on it.</summary>
  private sealed class ObservedGate : ISchemaReadyGate {
    private readonly SchemaReadyGate _inner = new();
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;
    public bool IsReady => _inner.IsReady;
    public void MarkReady() => _inner.MarkReady();

    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      return _inner.WaitForReadyAsync(cancellationToken);
    }
  }

  /// <summary>A schema gate that never opens and announces when a worker starts waiting on it.</summary>
  private sealed class ParkedGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;
    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
  }
}
