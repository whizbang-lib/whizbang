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
/// The maintenance sweep must actually consult the housekeeping gate, not merely have one available.
/// </summary>
/// <remarks>
/// <para>
/// A coordinator that nothing calls is inert. These tests drive the worker's real cycle and assert
/// on whether the sweep ran, so the gate cannot regress into decoration — the failure mode is a
/// green unit test for a policy object beside a worker that never asks it anything.
/// </para>
/// <para>
/// The sweep contends with the statement that marks work complete, so running it mid-drain stalls
/// every worker at the commit until it finishes. Deferring costs nothing; cleanup has no deadline.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MaintenanceWorkerHousekeepingGateTests {

  private sealed class GateFakeCoordinator(ServiceBacklog? backlog, bool throwOnSweep = false) : IWorkCoordinator {
    public int SweepCount { get; private set; }

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default)
      => ValueTask.FromResult(backlog);

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default) {
      SweepCount++;
      return throwOnSweep
        ? throw new InvalidOperationException("sweep failed")
        : Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private static (MaintenanceWorker Worker, GateFakeCoordinator Coord) _build(
      ServiceBacklog? backlog, HousekeepingCoordinator housekeeping, bool throwOnSweep = false) {
    var coord = new GateFakeCoordinator(backlog, throwOnSweep);
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance,
      metrics: null,
      housekeeping: housekeeping);
    return (worker, coord);
  }

  [Test]
  public async Task TheSweepIsSkippedWhileTheServiceIsDrainingAsync() {
    var (worker, coord) = _build(
      new ServiceBacklog { UnprocessedInboxRows = 34_033, ActiveLeasedRows = 1_870 },
      new HousekeepingCoordinator(new HousekeepingCoordinator.Settings()));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SweepCount).IsEqualTo(0)
      .Because("the sweep takes locks the completion path needs, so starting it mid-drain queues "
             + "every worker's commit behind it — the whole point of the gate is that this call "
             + "does not happen");
  }

  [Test]
  public async Task TheSweepRunsOnceTheServiceIsSettledAsync() {
    var (worker, coord) = _build(
      new ServiceBacklog { UnprocessedInboxRows = 0, ActiveLeasedRows = 0 },
      new HousekeepingCoordinator(new HousekeepingCoordinator.Settings()));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SweepCount).IsEqualTo(1)
      .Because("a gate that never opens is a disabled feature — deferral is only correct if the "
             + "sweep still runs when the service is quiet");
  }

  [Test]
  public async Task TheSweepIsSkippedWhileIntegrityWorkHoldsTheSlotAsync() {
    var housekeeping = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());
    housekeeping.TryBegin(HousekeepingCoordinator.Activity.Integrity, backlog: null);
    var (worker, coord) = _build(new ServiceBacklog(), housekeeping);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SweepCount).IsEqualTo(0)
      .Because("both walk the same tables — overlapping them puts housekeeping in contention with "
             + "housekeeping on top of whatever live work already competes for those locks");
  }

  [Test]
  public async Task TheSlotIsReleasedSoLaterCyclesStillRunAsync() {
    var housekeeping = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());
    var (worker, coord) = _build(new ServiceBacklog(), housekeeping);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SweepCount).IsEqualTo(2)
      .Because("holding the slot past the cycle would block every future sweep permanently, which "
             + "is strictly worse than the contention the gate was added to prevent");
  }

  [Test]
  public async Task TheSlotIsReleasedEvenWhenTheSweepThrowsAsync() {
    var housekeeping = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());
    var (worker, _) = _build(new ServiceBacklog(), housekeeping, throwOnSweep: true);

    try { await worker.RunMaintenanceOnceAsync(CancellationToken.None); } catch (InvalidOperationException) { }

    var after = housekeeping.TryBegin(HousekeepingCoordinator.Activity.Maintenance, new ServiceBacklog());
    await Assert.That(after.Granted).IsTrue()
      .Because("a failed sweep that never releases its slot disables maintenance for the lifetime "
             + "of the process — the release has to be in a finally, not on the success path");
  }

  [Test]
  public async Task AWorkerWithNoGateKeepsPriorBehaviorAsync() {
    var (worker, coord) = _build(
      new ServiceBacklog { UnprocessedInboxRows = 99_999 }, housekeeping: null!);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SweepCount).IsEqualTo(1)
      .Because("hosts that never registered the coordinator must keep the behavior they have "
             + "today; a missing collaborator must not silently switch maintenance off");
  }
}
