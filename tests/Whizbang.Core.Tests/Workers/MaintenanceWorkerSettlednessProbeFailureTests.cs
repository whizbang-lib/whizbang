using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// What the maintenance cycle does when the settledness probe itself fails.
/// <para>
/// The gate defers the sweep while the service is busy, and it learns "busy" from one query. If a
/// failed query counted as busy, one broken read would disable cleanup for the life of the
/// process — the sweep would defer forever while the store grew, and every deferral would look
/// like the gate working correctly. Unmeasured is therefore NOT busy: the probe failure is logged
/// and the cycle proceeds on a null backlog.
/// </para>
/// <para>
/// Cancellation is the one exception that must still travel. A shutdown arriving during the probe
/// is not a broken query, and swallowing it would run a sweep the host is trying to stop.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MaintenanceWorkerSettlednessProbeFailureTests {

  [Test]
  public async Task AFailedProbe_StillSweepsRatherThanDeferringForeverAsync() {
    var (worker, coord) = _build(new InvalidOperationException("backlog query failed"));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SweepCount).IsEqualTo(1)
      .Because("treating an unmeasured backlog as busy would let one broken query disable cleanup "
             + "for the life of the process, and every deferral would look like the gate working");
  }

  [Test]
  public async Task AProbeCanceledByShutdown_PropagatesInsteadOfSweepingAsync() {
    var (worker, coord) = _build(new OperationCanceledException());

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("shutdown is not a broken query — swallowing it starts a sweep the host is trying "
             + "to stop, and the sweep takes the locks the completion path needs");
    await Assert.That(coord.SweepCount).IsEqualTo(0);
  }

  private static (MaintenanceWorker Worker, ProbeFailingCoordinator Coord) _build(Exception probeFailure) {
    var coord = new ProbeFailingCoordinator(probeFailure);
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance,
      metrics: null,
      housekeeping: new HousekeepingCoordinator(new HousekeepingCoordinator.Settings()));
    return (worker, coord);
  }

  /// <summary>Fails the settledness probe; everything else behaves.</summary>
  private sealed class ProbeFailingCoordinator(Exception probeFailure) : IWorkCoordinator {
    public int SweepCount { get; private set; }

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default)
      => throw probeFailure;

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default) {
      SweepCount++;
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
