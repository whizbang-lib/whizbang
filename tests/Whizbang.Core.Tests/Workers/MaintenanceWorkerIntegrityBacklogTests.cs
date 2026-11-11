// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2256: the open repair backlog is read by the maintenance tick of the instance whose turn it is to do the
/// maintainer's work, and by no other. Every instance reads the same shared ledger, so a gauge every instance reported
/// would count the backlog once per instance, and the grouped read would run N times for one answer. An instance that
/// is not the holder reports no series at all, never a zero, and one that loses the duty withdraws what it reported.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
public class MaintenanceWorkerIntegrityBacklogTests {

  private static readonly LedgerBacklogSnapshot _backlog = new() {
    Reported = true,
    Buckets = [new LedgerBacklogCount("orders", LedgerBacklogStates.OPEN, 45_000)],
    OutboundDeliveries = 4_100,
  };

  private sealed class BacklogCoordinator : IWorkCoordinator {
    public LedgerBacklogSnapshot Backlog { get; set; } = _backlog;
    public Exception? Throw { get; set; }
    public int BacklogReads { get; private set; }
    public int MaxAttemptsSeen { get; private set; } = -1;
    public IReadOnlyList<MaintenanceResult> Results { get; init; } = [];

    public Task<LedgerBacklogSnapshot> GetIntegrityLedgerBacklogAsync(int maxRepairAttempts, CancellationToken cancellationToken = default) {
      BacklogReads++;
      MaxAttemptsSeen = maxRepairAttempts;
      return Throw is null ? Task.FromResult(Backlog) : Task.FromException<LedgerBacklogSnapshot>(Throw);
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(Results);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static (MaintenanceWorker Worker, StreamIntegrityMetrics Metrics) _build(
      BacklogCoordinator coordinator, DutyHolderWorker? duty = null, WhizbangRunPermit? permit = null,
      bool withIntegrity = true) {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton(metrics);
    if (withIntegrity) {
      services.AddSingleton(Options.Create(new StreamIntegrityOptions { MaxRepairAttemptsPerBucket = 6 }));
    }
    if (duty is not null) {
      services.AddSingleton(duty);
    }
    if (permit is not null) {
      services.AddSingleton(permit);
    }
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);
    return (worker, metrics);
  }

  [Test]
  public async Task TheMaintainer_PublishesTheBacklog_ReadAgainstTheRepairBudgetAsync() {
    var coordinator = new BacklogCoordinator();
    var duty = await Whizbang.Core.Tests.Startup.MaintainerDutyHolders.ElectedAsync(grants: true);
    var (worker, metrics) = _build(coordinator, duty);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(metrics.CurrentLedgerBacklogForTest).IsEqualTo(_backlog)
      .Because("the holder of the maintainer duty is the one instance that reports the backlog");
    await Assert.That(coordinator.MaxAttemptsSeen).IsEqualTo(6)
      .Because("exhausted is measured against the configured repair budget");
  }

  [Test]
  public async Task AnInstanceThatIsNotTheMaintainer_NeitherReadsNorReportsTheBacklogAsync() {
    var coordinator = new BacklogCoordinator();
    var duty = await Whizbang.Core.Tests.Startup.MaintainerDutyHolders.ElectedAsync(grants: false);
    var (worker, metrics) = _build(coordinator, duty);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.BacklogReads).IsEqualTo(0)
      .Because("the grouped read runs once per service, not once per instance");
    await Assert.That(metrics.CurrentLedgerBacklogForTest.Reported).IsFalse()
      .Because("a second reporter would double the backlog in any sum across instances");
  }

  [Test]
  public async Task AnInstanceThatLosesItsTurn_WithdrawsTheBacklogItReportedAsync() {
    var coordinator = new BacklogCoordinator();
    var permit = new WhizbangRunPermit();
    var (worker, metrics) = _build(coordinator, permit: permit);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await Assert.That(metrics.CurrentLedgerBacklogForTest.Reported).IsTrue()
      .Because("setup: without role assignment every running instance has a turn, so this one reported");

    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(metrics.CurrentLedgerBacklogForTest.Reported).IsFalse()
      .Because("a reading left behind by an instance that no longer has the turn goes stale and doubles the new holder's");
    await Assert.That(coordinator.BacklogReads).IsEqualTo(1);
  }

  [Test]
  public async Task AFailedRead_WithdrawsTheReading_AndTheCycleCarriesOnAsync() {
    var coordinator = new BacklogCoordinator();
    var (worker, metrics) = _build(coordinator);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    coordinator.Throw = new InvalidOperationException("ledger unavailable");
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(metrics.CurrentLedgerBacklogForTest.Reported).IsFalse()
      .Because("a reading that can no longer be refreshed is stale; no series beats a frozen one");
  }

  [Test]
  public async Task ACanceledRead_StopsTheCycleAsync() {
    var coordinator = new BacklogCoordinator { Throw = new OperationCanceledException() };
    var (worker, _) = _build(coordinator);

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("a canceled read is a stopping host, and the sweep that follows must not run");
  }

  [Test]
  public async Task WithoutStreamIntegrity_TheBacklogIsNotReadAsync() {
    var coordinator = new BacklogCoordinator();
    var (worker, metrics) = _build(coordinator, withIntegrity: false);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.BacklogReads).IsEqualTo(0);
    await Assert.That(metrics.CurrentLedgerBacklogForTest.Reported).IsFalse();
  }

  [Test]
  public async Task TheLedgerRowCapTrim_CountsItsRowsAsForgottenAsync() {
    var coordinator = new BacklogCoordinator {
      Results = [
        new MaintenanceResult("trim_integrity_ledger", 12, 1.0, "ok"),
        new MaintenanceResult("reap_expired_perspective_rows", 900, 1.0, "ok"),
      ],
    };
    var (worker, metrics) = _build(coordinator);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var closed = ProbeMeterReader.ReadSeries(metrics.LedgerBucketsClosed.Meter, "whizbang.stream_integrity.ledger_buckets_closed")
      .Where(s => Equals(s.Tags.GetValueOrDefault("how"), "forgotten")).Sum(s => s.Value);
    await Assert.That(closed).IsEqualTo(12L)
      .Because("the row cap removes buckets without healing them, and the open gauge falls by as many");
  }
}
