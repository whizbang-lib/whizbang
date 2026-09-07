using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for the disabled-worker path of <see cref="LeaseRenewalWorker"/> that the primary
/// (<see cref="LeaseRenewalWorkerCapTests"/>) suite never toggles: <c>Enabled = false</c> both at
/// the hosted-service level (<c>ExecuteAsync</c> parks instead of running the flusher's stopped
/// signal) and at the per-flush level (a batch that reaches the flush callback anyway must bail
/// before ever touching the coordinator). A lease renewal worker keeps a claim alive; a disabled
/// instance that kept renewing anyway would mean an operator's killswitch for a misbehaving
/// renewal path silently does nothing.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class LeaseRenewalWorkerCoverageTests {

  private sealed class _recordingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<(WorkCategory Category, IReadOnlyList<Guid> Ids)> Calls { get; } = [];

    Task<int> IWorkCoordinator.RenewLeasesAsync(WorkCategory category, IReadOnlyList<Guid> ids, int leaseSeconds, CancellationToken cancellationToken) {
      lock (Calls) {
        Calls.Add((category, ids));
      }
      return Task.FromResult(ids.Count);
    }
  }

  /// <summary>What breaks: the disabled hosted-service loop must still resolve cleanly on
  /// shutdown. If the infinite park never let go of the stopping token, a disabled renewal worker
  /// would hang the whole host's shutdown sequence.</summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_WhenDisabled_ParksThenStopsCleanlyOnShutdownAsync(CancellationToken testToken) {
    var services = new ServiceCollection().BuildServiceProvider();
    var worker = new LeaseRenewalWorker(
      services.GetRequiredService<IServiceScopeFactory>(),
      Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      Options.Create(new LeaseRenewalWorkerOptions { Enabled = false }),
      NullLogger<LeaseRenewalWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask;
    await worker.StopAsync(CancellationToken.None);

    await executeTask!.WaitAsync(TimeSpan.FromSeconds(5), testToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("a disabled worker's infinite park exists to keep the hosted service alive without polling, not to survive past StopAsync");
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("stopping a parked, disabled worker is an ordinary shutdown, not a crash");
  }

  /// <summary>What breaks: the killswitch is checked again inside the flush callback itself,
  /// because the batch flusher's coalescing loop starts in the constructor and can pick up work
  /// enqueued before (or despite) the disabled flag. If that check were missing, a "disabled"
  /// worker would still extend leases behind an operator's back.</summary>
  [Test]
  [Timeout(30000)]
  public async Task FlushBatchAsync_WhenDisabled_NeverReachesTheCoordinatorAsync(CancellationToken testToken) {
    var coordinator = new _recordingCoordinator();
    var services = new ServiceCollection()
      .AddSingleton<IWorkCoordinator>(coordinator)
      .BuildServiceProvider();
    var worker = new LeaseRenewalWorker(
      services.GetRequiredService<IServiceScopeFactory>(),
      Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      Options.Create(new LeaseRenewalWorkerOptions {
        Enabled = false,
        Flusher = new BatchFlusherOptions {
          MaxBatchSize = 10,
          CoalesceWindowMs = 5,
          ImmediateFlushThreshold = 1,
          ChannelCapacity = 100,
          DrainTimeoutMs = 2000,
        },
      }),
      NullLogger<LeaseRenewalWorker>.Instance);
    // No LeaseRegistry supplied: if the disabled check were bypassed, the "no registry wired"
    // fallback would unconditionally submit this id to RenewLeasesAsync, making the assertion
    // below meaningful rather than accidentally true for an unrelated reason.

    var workId = (Guid)TrackedGuid.NewMedo();
    await worker.EnqueueAsync(WorkCategory.Inbox, workId, testToken);
    // The flusher's coalescing loop started in the constructor; StopAsync drains it and waits for
    // the loop to exit, which only happens once the (disabled, so immediately-returning) flush for
    // our enqueued item has run.
    await worker.StopAsync(testToken);

    await Assert.That(coordinator.Calls).IsEmpty()
      .Because("a disabled worker must be a true no-op — reaching the coordinator here would mean the killswitch doesn't actually stop lease extension");
  }
}
