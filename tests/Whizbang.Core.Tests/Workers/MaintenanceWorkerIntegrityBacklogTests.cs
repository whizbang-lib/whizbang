// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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

    public IntegritySealReading Seals { get; set; } = new() {
      Reported = true,
      Lanes = [new LaneSeal(Guid.Empty, 300, 12)],
      SealRecheckCoverageRatio = 0.5,
    };
    public (TimeSpan Lookback, TimeSpan Every, DateTimeOffset Now)? SealReadArgs { get; private set; }

    public Task<CutsAwaitingSeal> GetCutsAwaitingSealAsync(int cap, CancellationToken cancellationToken = default) =>
      Task.FromResult(new CutsAwaitingSeal {
        Reported = true,
        Tasks = new Dictionary<string, PendingRows>(StringComparer.Ordinal) { ["truncate"] = new(7, false) },
      });

    public Task<IntegritySealReading> GetIntegritySealReadingAsync(
        TimeSpan lookback, TimeSpan recheckEvery, DateTimeOffset now, CancellationToken cancellationToken = default) {
      SealReadArgs = (lookback, recheckEvery, now);
      return Task.FromResult(Seals);
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
      bool withIntegrity = true, bool withMetrics = true, TimeProvider? clock = null, MaintenanceMetrics? maintenance = null) {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (withMetrics) {
      services.AddSingleton(metrics);
    }
    if (maintenance is not null) {
      services.AddSingleton(maintenance);
    }
    if (withIntegrity) {
      services.AddSingleton(Options.Create(new StreamIntegrityOptions {
        MaxRepairAttemptsPerBucket = 6,
        SealRecheck = { Enabled = true, Lookback = TimeSpan.FromDays(20), RecheckEvery = TimeSpan.FromDays(3) },
      }));
    }
    if (duty is not null) {
      services.AddSingleton(duty);
    }
    if (permit is not null) {
      services.AddSingleton(permit);
    }
    if (clock is not null) {
      services.AddSingleton(clock);
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
  public async Task WithoutStreamIntegrityMetrics_NothingIsReadOrRecorded_AndTheCycleRunsAsync() {
    var coordinator = new BacklogCoordinator { Results = [new MaintenanceResult("trim_integrity_ledger", 5, 1.0, "ok")] };
    var (worker, _) = _build(coordinator, withMetrics: false);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var paused = new WhizbangRunPermit();
    await paused.ChangeAsync(RunState.Paused, CancellationToken.None);
    var (idle, _) = _build(coordinator, permit: paused, withMetrics: false);
    await idle.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.BacklogReads).IsEqualTo(0)
      .Because("with nowhere to publish the backlog, the grouped read is not worth its cost");
  }

  [Test]
  public async Task TheLedgerRowCapTrim_CountsItsRowsAsForgottenAsync() {
    var coordinator = new BacklogCoordinator {
      Results = [
        new MaintenanceResult("trim_integrity_ledger", 0, 1.0, "ok"),
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

  /// <summary>#2365: the seal gauges are read by the holder, as the backlog is, on its clock and the re-check's window.</summary>
  [Test]
  public async Task TheMaintainer_PublishesTheSealReading_OnItsClock_OverTheRecheckWindowAsync() {
    var coordinator = new BacklogCoordinator();
    var at = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    var (worker, metrics) = _build(coordinator, clock: new FakeTimeProvider(at));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.SealReadArgs!.Value).IsEqualTo((TimeSpan.FromDays(20), TimeSpan.FromDays(3), at));
    var frontier = ProbeMeterReader.ReadSeries(metrics.RepairsUnhealable.Meter, "whizbang.stream_integrity.epoch_frontier");
    await Assert.That(frontier.Single().Value).IsEqualTo(300L);
    var enabled = ProbeMeterReader.ReadSeries(metrics.RepairsUnhealable.Meter, "whizbang.stream_integrity.recheck_enabled")
      .ToDictionary(m => (string)m.Tags["check"]!, m => m.Value);
    await Assert.That(enabled["seal_recheck"]).IsEqualTo(1L).Because("every tick publishes whether each check is on");
    await Assert.That(enabled["full_history_compare"]).IsEqualTo(0L);
  }

  [Test]
  public async Task WithNoClockRegistered_TheSealReadingIsTakenOnTheSystemClockAsync() {
    var coordinator = new BacklogCoordinator();
    var (worker, metrics) = _build(coordinator);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    // The test reads no clock of its own: a reading was taken, at a real UTC instant, with nothing registered.
    await Assert.That(coordinator.SealReadArgs!.Value.Now).IsNotEqualTo(default(DateTimeOffset))
      .Because("with no clock registered the worker falls back to the system clock rather than skipping the reading");
    await Assert.That(coordinator.SealReadArgs!.Value.Now.Offset).IsEqualTo(TimeSpan.Zero);
    await Assert.That(ProbeMeterReader.ReadSeries(metrics.RepairsUnhealable.Meter, "whizbang.stream_integrity.epoch_frontier").Count)
      .IsEqualTo(1);
  }

  [Test]
  public async Task AnInstanceThatLosesItsTurn_OrFailsTheRead_WithdrawsTheSealReadingAsync() {
    var coordinator = new BacklogCoordinator();
    var permit = new WhizbangRunPermit();
    var (worker, metrics) = _build(coordinator, permit: permit);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await Assert.That(ProbeMeterReader.ReadSeries(metrics.RepairsUnhealable.Meter, "whizbang.stream_integrity.epoch_frontier"))
      .IsEmpty().Because("a lane's seal is reported by the holder alone");

    var failing = new BacklogCoordinator();
    var maintenance = new MaintenanceMetrics(
      new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var (failingWorker, failingMetrics) = _build(failing, maintenance: maintenance);
    await failingWorker.RunMaintenanceOnceAsync(CancellationToken.None);
    failing.Throw = new InvalidOperationException("ledger unavailable");
    await failingWorker.RunMaintenanceOnceAsync(CancellationToken.None);
    await Assert.That(ProbeMeterReader.ReadSeries(failingMetrics.RepairsUnhealable.Meter, "whizbang.stream_integrity.epoch_frontier"))
      .IsEmpty().Because("a reading that can no longer be refreshed is stale");
    await Assert.That(ProbeMeterReader.ReadSeries(maintenance.RowsAffected.Meter, "whizbang.maintenance.eligible_not_yet_deleted"))
      .IsEmpty().Because("and so is the count of the cuts the seal holds");
  }

  /// <summary>#2365: the holder reads the streams the seal gate holds, and an instance that loses its turn withdraws them.</summary>
  [Test]
  public async Task TheMaintainer_PublishesTheCutsTheSealHolds_AndAnInstanceThatLosesItsTurnWithdrawsThemAsync() {
    var coordinator = new BacklogCoordinator();
    var permit = new WhizbangRunPermit();
    var maintenance = new MaintenanceMetrics(
      new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var (worker, _) = _build(coordinator, permit: permit, maintenance: maintenance);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    var held = ProbeMeterReader.ReadSeries(maintenance.RowsAffected.Meter, "whizbang.maintenance.eligible_not_yet_deleted");
    await Assert.That(held.Single().Value).IsEqualTo(7L);

    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await Assert.That(ProbeMeterReader.ReadSeries(maintenance.RowsAffected.Meter, "whizbang.maintenance.eligible_not_yet_deleted"))
      .IsEmpty();
  }
}
