// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// What the maintenance cycle tells an operator when the housekeeping gate defers or forces the sweep.
/// <para>
/// A deferral reports the service's backlog so the reader can see why the sweep waited. A backlog the
/// probe did not measure is reported as -1 in every count, never as zeros: "unmeasured" must not read as
/// "empty". A deferral can see an unmeasured backlog (another activity already holds the slot, which
/// refuses before settledness is asked), and the forced pass after the deferral budget reports the
/// counts that kept the service from settling.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MaintenanceWorkerHousekeepingLogTests {
  private const int DEFERRED = 47;
  private const int FORCED = 48;

  [Test]
  public async Task ADeferralWithAnUnmeasuredBacklog_ReportsEveryCountAsUnmeasuredAsync() {
    var housekeeping = _gate();
    housekeeping.TryBegin(HousekeepingCoordinator.Activity.Integrity, backlog: null);
    var (worker, coord, logger) = _build(backlog: null, housekeeping);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SweepCount).IsEqualTo(0);
    var deferred = _single(logger, DEFERRED);
    await Assert.That(deferred.Message).Contains("has -1 unprocessed inbox row(s), -1 active lease(s), -1 pending outbox row(s) and -1 pending perspective event(s)")
      .Because("a backlog nobody measured must not read as an empty one");
  }

  [Test]
  public async Task ADeferralWithAMeasuredBacklog_ReportsItsCountsAsync() {
    var (worker, coord, logger) = _build(_busy, _gate());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SweepCount).IsEqualTo(0);
    var deferred = _single(logger, DEFERRED);
    await Assert.That(deferred.Message).Contains("has 34033 unprocessed inbox row(s), 1870 active lease(s), 12 pending outbox row(s) and 5 pending perspective event(s)");
  }

  [Test]
  public async Task TheForcedPassAfterTheDeferralBudget_ReportsTheCountsThatKeptTheServiceBusyAsync() {
    var (worker, coord, logger) = _build(_busy, _gate(maxDeferrals: 0));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SweepCount).IsEqualTo(1)
      .Because("the budget is spent, so the sweep runs although the service is busy");
    var forced = _single(logger, FORCED);
    await Assert.That(forced.Message).Contains("(34033 unprocessed inbox row(s), 1870 active lease(s), 12 pending outbox row(s), 5 pending perspective event(s))");
  }

  private static readonly ServiceBacklog _busy = new() {
    UnprocessedInboxRows = 34_033,
    ActiveLeasedRows = 1_870,
    PendingOutboxRows = 12,
    PendingPerspectiveRows = 5,
  };

  private static HousekeepingCoordinator _gate(int maxDeferrals = 6)
    => new(new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero, MaxConsecutiveDeferrals = maxDeferrals });

  private static FakeLogRecord _single(FakeLogger<MaintenanceWorker> logger, int eventId)
    => logger.Collector.GetSnapshot().Single(r => r.Id.Id == eventId);

  private static (MaintenanceWorker Worker, BacklogCoordinator Coord, FakeLogger<MaintenanceWorker> Logger) _build(
      ServiceBacklog? backlog, HousekeepingCoordinator housekeeping) {
    var coord = new BacklogCoordinator(backlog);
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var logger = new FakeLogger<MaintenanceWorker>();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger,
      metrics: null,
      housekeeping: housekeeping);
    return (worker, coord, logger);
  }

  /// <summary>Reports a fixed backlog and counts sweeps; everything else behaves.</summary>
  private sealed class BacklogCoordinator(ServiceBacklog? backlog) : IWorkCoordinator {
    public int SweepCount { get; private set; }

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default)
      => ValueTask.FromResult(backlog);

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
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
