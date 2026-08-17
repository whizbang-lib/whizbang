using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Phase H step 9 slice 6 — RED-first locks for <see cref="LeaseRenewalWorker"/>'s
/// <c>MaxRenewalsPerWork</c> cap behavior + <see cref="LeaseRegistry"/> integration.
/// </summary>
/// <remarks>
/// Without the cap, a hung handler whose dispatch worker enqueues lease renewals every cycle
/// gets its DB lease extended forever. The slice 6 change: when the in-process LeaseHandle's
/// <see cref="LeaseHandle.TryExtendDeadline"/> returns false (cap hit OR disposed), the
/// renewal worker skips submitting that work_id to <c>RenewLeasesAsync</c>. The SQL lease
/// expires naturally → <c>claim_orphaned_*</c> re-issues → step 8's attempts bump → eventual
/// dead-letter.
/// </remarks>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class LeaseRenewalWorkerCapTests {

  /// <summary>
  /// Records renewal submissions. The list is written on the worker's flush thread and read from
  /// the test thread, so every access goes through the lock and callers take a SNAPSHOT — reading
  /// the live list cross-thread is a data race regardless of how the wait is written.
  /// <see cref="WaitForAsync"/> lets a test await a condition instead of spinning for it.
  /// </summary>
  private sealed class FakeCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly List<(WorkCategory Cat, Guid[] Ids)> _calls = [];
    private readonly List<(Func<IReadOnlyList<(WorkCategory Cat, Guid[] Ids)>, bool> Predicate, TaskCompletionSource Signal)> _waiters = [];

    public IReadOnlyList<(WorkCategory Cat, Guid[] Ids)> Snapshot() {
      lock (_lock) {
        return [.. _calls];
      }
    }

    public int RenewalsFor(Guid workId) {
      lock (_lock) {
        return _calls.Sum(c => c.Ids.Count(id => id == workId));
      }
    }

    /// <summary>
    /// Completes as soon as the recorded submissions satisfy <paramref name="predicate"/>, which
    /// is evaluated under the lock on every submission. Replaces spinning on the live list: the
    /// wait ends the instant the condition holds, and the caller never touches shared state.
    /// </summary>
    public Task WaitForAsync(Func<IReadOnlyList<(WorkCategory Cat, Guid[] Ids)>, bool> predicate, TimeSpan timeout) {
      TaskCompletionSource signal;
      lock (_lock) {
        if (predicate([.. _calls])) {
          return Task.CompletedTask;
        }
        signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((predicate, signal));
      }
      return signal.Task.WaitAsync(timeout);
    }

    Task<int> IWorkCoordinator.RenewLeasesAsync(WorkCategory category, IReadOnlyList<Guid> ids, int leaseSeconds, CancellationToken cancellationToken) {
      lock (_lock) {
        _calls.Add((category, ids.ToArray()));
        IReadOnlyList<(WorkCategory Cat, Guid[] Ids)> snapshot = [.. _calls];
        for (var i = _waiters.Count - 1; i >= 0; i--) {
          if (_waiters[i].Predicate(snapshot)) {
            // RunContinuationsAsynchronously, so no continuation runs inline under the lock.
            _ = _waiters[i].Signal.TrySetResult();
            _waiters.RemoveAt(i);
          }
        }
      }
      return Task.FromResult(ids.Count);
    }
  }

  /// <summary>
  /// Drives a throwaway work id through the flusher and waits for it. Because the flusher
  /// processes submissions in order, the sentinel arriving proves everything enqueued before it
  /// has already been through the renewal path — which is what lets a test assert that something
  /// did NOT happen without sleeping and hoping.
  /// </summary>
  private static async Task _drainAsync(LeaseRenewalWorker worker, FakeCoordinator coord, FakeTimeProvider time,
                                        LeaseRegistry? registry, CancellationToken ct) {
    var sentinel = (Guid)TrackedGuid.NewMedo();
    LeaseHandle? handle = null;
    if (registry is not null) {
      handle = _newHandle(time, registry, sentinel, maxRenewals: 1);
    }
    try {
      await worker.EnqueueAsync(WorkCategory.Inbox, sentinel, ct);
      await coord.WaitForAsync(c => c.Any(x => x.Ids.Contains(sentinel)), TimeSpan.FromSeconds(5));
    } finally {
      handle?.Dispose();
    }
  }

  private static (LeaseRenewalWorker worker, FakeCoordinator coord, FakeTimeProvider time, LeaseRegistry registry) _build(int maxRenewals) {
    var coord = new FakeCoordinator();
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
    var registry = new LeaseRegistry();
    var sp = new ServiceCollection()
      .AddSingleton<IWorkCoordinator>(coord)
      .BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new LeaseRenewalWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new LeaseRenewalWorkerOptions {
        LeaseSeconds = 60,
        Flusher = new BatchFlusherOptions { MaxBatchSize = 100, CoalesceWindowMs = 5, ImmediateFlushThreshold = 1, ChannelCapacity = 1000 }
      }),
      NullLogger<LeaseRenewalWorker>.Instance,
      registry,
      time);
    return (worker, coord, time, registry);
  }

  private static LeaseHandle _newHandle(FakeTimeProvider time, LeaseRegistry registry, Guid workId, int maxRenewals) {
    var handle = new LeaseHandle(
      workId: workId,
      category: WorkCategory.Inbox,
      deadline: time.GetUtcNow() + TimeSpan.FromMinutes(5),
      maxRenewals: maxRenewals,
      timeProvider: time,
      linkedTokens: []);
    registry.Register(handle);
    return handle;
  }

  [Test]
  public async Task Renewal_BumpsHandleCountAndCallsRenewLeasesAsync() {
    var (worker, coord, time, registry) = _build(maxRenewals: 6);
    var workId = (Guid)TrackedGuid.NewMedo();
    using var handle = _newHandle(time, registry, workId, maxRenewals: 6);
    using var cts = new CancellationTokenSource();

    await worker.StartAsync(cts.Token);
    await worker.EnqueueAsync(WorkCategory.Inbox, workId, cts.Token);

    // Await the submission. The previous `while (...) await Task.Yield()` spin was not just
    // wasteful: Task.Yield reschedules straight back onto the thread pool, so a 5-second spin
    // competes for the very pool threads the flush needs, making the thing it waits for slower
    // exactly when the machine is busiest.
    await coord.WaitForAsync(c => c.Count >= 1, TimeSpan.FromSeconds(5));
    var calls = coord.Snapshot();

    await Assert.That(calls.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(calls[0].Ids).Contains(workId);
    await Assert.That(handle.RenewalCount).IsEqualTo(1)
      .Because("a successful DB renewal must also extend the in-process LeaseHandle's deadline");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task RenewalCount_AtCap_StopsSubmittingToRenewLeasesAsync() {
    var (worker, coord, time, registry) = _build(maxRenewals: 3);
    var workId = (Guid)TrackedGuid.NewMedo();
    using var handle = _newHandle(time, registry, workId, maxRenewals: 3);
    using var cts = new CancellationTokenSource();

    await worker.StartAsync(cts.Token);

    // Enqueue 4 renewals — only the first 3 should reach RenewLeasesAsync.
    for (var i = 0; i < 4; i++) {
      await worker.EnqueueAsync(WorkCategory.Inbox, workId, cts.Token);
      // Drain each enqueue separately so the cap engages incrementally. Iteration 4 must NOT
      // renew, so there is no count to wait for — a sentinel through the same in-order flusher
      // proves it was processed and skipped.
      var expected = Math.Min(i + 1, 3);
      if (expected > coord.RenewalsFor(workId)) {
        await coord.WaitForAsync(c => c.Sum(x => x.Ids.Count(id => id == workId)) >= expected,
                                 TimeSpan.FromSeconds(5));
      } else {
        await _drainAsync(worker, coord, time, registry, cts.Token);
      }
    }

    var totalDbRenewalsForThisWorkId = coord.RenewalsFor(workId);
    await Assert.That(totalDbRenewalsForThisWorkId).IsEqualTo(3)
      .Because("4 renewal requests with cap=3 — only 3 should reach RenewLeasesAsync; the 4th must be skipped so the lease expires naturally");
    await Assert.That(handle.RenewalCount).IsEqualTo(3);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task DisposedHandle_SkippedAtRenewalAsync() {
    var (worker, coord, time, registry) = _build(maxRenewals: 6);
    var workId = (Guid)TrackedGuid.NewMedo();
    var handle = _newHandle(time, registry, workId, maxRenewals: 6);
    using var cts = new CancellationTokenSource();

    await worker.StartAsync(cts.Token);
    handle.Dispose();  // auto-removes from registry
    await worker.EnqueueAsync(WorkCategory.Inbox, workId, cts.Token);

    // Prove the flush ran rather than sleeping and assuming it did: a sentinel enqueued after
    // the disposed id must come out the other side, and the flusher preserves order.
    await _drainAsync(worker, coord, time, registry, cts.Token);

    var renewedThisWorkId = coord.RenewalsFor(workId);
    await Assert.That(renewedThisWorkId).IsEqualTo(0)
      .Because("a disposed handle (auto-removed from registry) must not be renewed in the DB");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task NullRegistry_FallsBackToLegacyBehaviorAsync() {
    // Backward-compat: when no registry is wired (e.g., test fixtures or pre-DI-rollout),
    // the worker should still renew unconditionally — no cap enforcement possible.
    var coord = new FakeCoordinator();
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
    var sp = new ServiceCollection()
      .AddSingleton<IWorkCoordinator>(coord)
      .BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new LeaseRenewalWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new LeaseRenewalWorkerOptions {
        Flusher = new BatchFlusherOptions { MaxBatchSize = 10, CoalesceWindowMs = 5, ImmediateFlushThreshold = 1, ChannelCapacity = 100 }
      }),
      NullLogger<LeaseRenewalWorker>.Instance,
      leaseRegistry: null,  // not wired
      timeProvider: time);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var workId = (Guid)TrackedGuid.NewMedo();
    await worker.EnqueueAsync(WorkCategory.Inbox, workId, cts.Token);

    await coord.WaitForAsync(c => c.Count >= 1, TimeSpan.FromSeconds(5));

    await Assert.That(coord.Snapshot().Count).IsGreaterThanOrEqualTo(1)
      .Because("when registry is null, the worker should renew unconditionally (legacy behavior)");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
