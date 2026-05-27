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

  private sealed class FakeCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<(WorkCategory Cat, Guid[] Ids)> RenewLeasesCalls { get; } = [];
    Task<int> IWorkCoordinator.RenewLeasesAsync(WorkCategory category, IReadOnlyList<Guid> ids, int leaseSeconds, CancellationToken cancellationToken) {
      RenewLeasesCalls.Add((category, ids.ToArray()));
      return Task.FromResult(ids.Count);
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

    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (coord.RenewLeasesCalls.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) {
      await Task.Yield();
    }

    await Assert.That(coord.RenewLeasesCalls.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(coord.RenewLeasesCalls[0].Ids).Contains(workId);
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
      // Allow the flusher to drain each enqueue separately so the cap engages incrementally.
      var sw = System.Diagnostics.Stopwatch.StartNew();
      while (coord.RenewLeasesCalls.Count(c => c.Ids.Contains(workId)) < Math.Min(i + 1, 3)
             && sw.Elapsed < TimeSpan.FromSeconds(2)) {
        await Task.Yield();
      }
    }

    // Allow any final flush to complete.
    await Task.Delay(100);

    var totalDbRenewalsForThisWorkId = coord.RenewLeasesCalls.Sum(c => c.Ids.Count(id => id == workId));
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

    // Wait briefly for any flush activity.
    await Task.Delay(200);

    var renewedThisWorkId = coord.RenewLeasesCalls.Sum(c => c.Ids.Count(id => id == workId));
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

    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (coord.RenewLeasesCalls.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) {
      await Task.Yield();
    }

    await Assert.That(coord.RenewLeasesCalls.Count).IsGreaterThanOrEqualTo(1)
      .Because("when registry is null, the worker should renew unconditionally (legacy behavior)");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
