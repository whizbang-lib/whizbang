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
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2261: the maintenance tick keeps the derived completion gauges. Every instance writes the buckets its own counters
/// opened and closed into the service's shared tally, so the figures are per service and survive a restart; the instance
/// whose turn it is to do the maintainer's work reads the tally over the rolling window and reports
/// <c>repair_progress_percent</c> and <c>backlog_eta_seconds</c>, as it reports the backlog; every instance reports
/// <c>verified_percent</c> from the seals it already reads. Each is switched off by configuration.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
public class MaintenanceWorkerIntegrityCompletionTests {
  private static readonly DateTimeOffset _now = new(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);
  private static readonly Guid _origin = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
  private static readonly (long, long)[] _theSameCountsTwice = [(4L, 1L), (4L, 1L)];
  private static readonly DateTimeOffset[] _halfAnHourBack = [_now.AddMinutes(-30)];

  private sealed class CompletionCoordinator : IWorkCoordinator {
    public LedgerGaugeSnapshot Summary { get; set; } = new() { Seals = [new OriginSeal(_origin, 51, 100)] };
    public LedgerBacklogSnapshot Backlog { get; set; } = new() {
      Reported = true,
      Buckets = [
        new LedgerBacklogCount("orders", LedgerBacklogStates.OPEN, 900),
        new LedgerBacklogCount("orders", LedgerBacklogStates.UNHEALABLE, 40),
      ],
    };
    public IntegrityLedgerTally? Tally { get; set; } = new(Opened: 100, Closed: 400);
    public bool TallyWriteSucceeds { get; set; } = true;
    public Exception? TallyWriteThrows { get; set; }
    public List<(DateTimeOffset At, long Opened, long Closed, DateTimeOffset KeepSince)> TallyWrites { get; } = [];
    public List<DateTimeOffset> TallyReads { get; } = [];

    public Task<LedgerGaugeSnapshot> GetIntegrityLedgerSummaryAsync(int maxRepairAttempts, CancellationToken cancellationToken = default) =>
      Task.FromResult(Summary);

    public Task<LedgerBacklogSnapshot> GetIntegrityLedgerBacklogAsync(int maxRepairAttempts, CancellationToken cancellationToken = default) =>
      Task.FromResult(Backlog);

    public Task<bool> IntegrityAddLedgerTallyAsync(
        DateTimeOffset at, long opened, long closed, DateTimeOffset keepSince, CancellationToken cancellationToken = default) {
      if (TallyWriteThrows is not null) {
        return Task.FromException<bool>(TallyWriteThrows);
      }
      TallyWrites.Add((at, opened, closed, keepSince));
      return Task.FromResult(TallyWriteSucceeds);
    }

    public Task<IntegrityLedgerTally?> GetIntegrityLedgerTallyAsync(DateTimeOffset since, CancellationToken cancellationToken = default) {
      TallyReads.Add(since);
      return Task.FromResult(Tally);
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static (MaintenanceWorker Worker, StreamIntegrityMetrics Metrics) _build(
      CompletionCoordinator coordinator, StreamIntegrityOptions? options = null, DutyHolderWorker? duty = null,
      bool withClock = true) {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton(metrics);
    if (withClock) {
      services.AddSingleton<TimeProvider>(new FakeTimeProvider(_now));
    }
    services.AddSingleton(Options.Create(options ?? new StreamIntegrityOptions()));
    if (duty is not null) {
      services.AddSingleton(duty);
    }
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);
    return (worker, metrics);
  }

  private static void _count(StreamIntegrityMetrics metrics, long opened, long closed) {
    metrics.LedgerBucketsOpened.Add(opened, new KeyValuePair<string, object?>(StreamIntegrityMetrics.HOW_TAG, StreamIntegrityMetrics.OPENED_NEW));
    metrics.LedgerBucketsClosed.Add(closed, new KeyValuePair<string, object?>(StreamIntegrityMetrics.HOW_TAG, StreamIntegrityMetrics.CLOSED_HEALED));
  }

  // ── verified_percent ─────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task EveryInstance_ReportsTheVerifiedPercent_FromTheSealsItReadsAsync() {
    var duty = await Whizbang.Core.Tests.Startup.MaintainerDutyHolders.ElectedAsync(grants: false);
    var (worker, metrics) = _build(new CompletionCoordinator(), duty: duty);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var reading = metrics.CurrentVerifiedPercentForTest;
    await Assert.That(reading.Reported).IsTrue()
      .Because("the seals are read on every instance, as sealed_through is reported on every instance");
    await Assert.That(reading.Origins.Single().Percent).IsEqualTo(50.0);
    await Assert.That(reading.AllOrigins).IsEqualTo(50.0);
  }

  [Test]
  public async Task TheVerifiedPercent_SwitchedOff_IsNotReportedAsync() {
    var (worker, metrics) = _build(new CompletionCoordinator(), new StreamIntegrityOptions { VerifiedPercentGaugeEnabled = false });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(metrics.CurrentVerifiedPercentForTest.Reported).IsFalse();
  }

  // ── the shared tally ─────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task EachTick_WritesWhatTheCountersAddedSinceTheLast_OnceAsync() {
    var coordinator = new CompletionCoordinator();
    var (worker, metrics) = _build(coordinator);
    _count(metrics, opened: 7, closed: 3);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    _count(metrics, opened: 0, closed: 2);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.TallyWrites.Count).IsEqualTo(2)
      .Because("a tick with nothing new to tally writes nothing");
    await Assert.That(coordinator.TallyWrites[0]).IsEqualTo((_now, 7L, 3L, _now.AddHours(-2)))
      .Because("stamped on this host's clock, and rows older than twice the window are pruned in the same call");
    await Assert.That((coordinator.TallyWrites[1].Opened, coordinator.TallyWrites[1].Closed)).IsEqualTo((0L, 2L));
  }

  [Test]
  public async Task WithoutARegisteredClock_TheTallyIsStampedOnTheSystemClockAsync() {
    var coordinator = new CompletionCoordinator();
    var (worker, metrics) = _build(coordinator, withClock: false);
    _count(metrics, opened: 1, closed: 1);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var write = coordinator.TallyWrites.Single();
    await Assert.That(write.At - write.KeepSince).IsEqualTo(TimeSpan.FromHours(2))
      .Because("a host that registers no clock still tallies, on the system clock, keeping twice the window");
  }

  [Test]
  public async Task ATallyNotWritten_IsWrittenOnTheNextTickAsync() {
    var coordinator = new CompletionCoordinator { TallyWriteSucceeds = false };
    var (worker, metrics) = _build(coordinator);
    _count(metrics, opened: 4, closed: 1);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    coordinator.TallyWriteSucceeds = true;
    coordinator.TallyWriteThrows = new InvalidOperationException("tally unavailable");
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    coordinator.TallyWriteThrows = null;
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.TallyWrites.Select(w => (w.Opened, w.Closed)).ToArray())
      .IsEquivalentTo(_theSameCountsTwice)
      .Because("a write that did not land, or failed, leaves the counts to the next tick rather than losing them");
  }

  [Test]
  public async Task ACanceledTallyWrite_StopsTheCycleAsync() {
    var coordinator = new CompletionCoordinator { TallyWriteThrows = new OperationCanceledException() };
    var (worker, metrics) = _build(coordinator);
    _count(metrics, opened: 1, closed: 0);

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>();
  }

  // ── repair_progress_percent and backlog_eta_seconds ──────────────────────────────────────────

  [Test]
  public async Task TheMaintainer_ReportsProgressAndEta_OverTheWindowAsync() {
    var coordinator = new CompletionCoordinator();
    var duty = await Whizbang.Core.Tests.Startup.MaintainerDutyHolders.ElectedAsync(grants: true);
    var (worker, metrics) = _build(coordinator, new StreamIntegrityOptions { RepairProgressWindowMinutes = 30 }, duty);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.TallyReads).IsEquivalentTo(_halfAnHourBack);
    var reading = metrics.CurrentRepairProgressForTest;
    await Assert.That(reading.Reported).IsTrue();
    await Assert.That(reading.ProgressPercent).IsEqualTo(400.0).Because("400 closed of 100 opened in the window");
    await Assert.That(reading.EtaSeconds).IsEqualTo(5_400.0)
      .Because("900 repairable open buckets at a net 300 per 30 minutes; the 40 unhealable ones are not repair's to close");
  }

  [Test]
  public async Task AnInstanceThatIsNotTheMaintainer_NeitherReadsTheTallyNorReportsProgress_ButStillWritesItsOwnAsync() {
    var coordinator = new CompletionCoordinator();
    var duty = await Whizbang.Core.Tests.Startup.MaintainerDutyHolders.ElectedAsync(grants: false);
    var (worker, metrics) = _build(coordinator, duty: duty);
    _count(metrics, opened: 2, closed: 1);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.TallyReads).IsEmpty();
    await Assert.That(metrics.CurrentRepairProgressForTest.Reported).IsFalse()
      .Because("one instance per service reports these, or a sum across instances would count them twice");
    await Assert.That(coordinator.TallyWrites.Count).IsEqualTo(1)
      .Because("the counts this instance made belong in the service's figures whoever reports them");
  }

  [Test]
  public async Task EachDerivedGauge_SwitchesOffOnItsOwnAsync() {
    var noProgress = new CompletionCoordinator();
    var (worker1, metrics1) = _build(noProgress, new StreamIntegrityOptions { RepairProgressGaugeEnabled = false });
    await worker1.RunMaintenanceOnceAsync(CancellationToken.None);

    var noEta = new CompletionCoordinator();
    var (worker2, metrics2) = _build(noEta, new StreamIntegrityOptions { BacklogEtaGaugeEnabled = false });
    await worker2.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(metrics1.CurrentRepairProgressForTest.ProgressPercent).IsNull();
    await Assert.That(metrics1.CurrentRepairProgressForTest.EtaSeconds).IsEqualTo(10_800.0);
    await Assert.That(metrics2.CurrentRepairProgressForTest.ProgressPercent).IsEqualTo(400.0);
    await Assert.That(metrics2.CurrentRepairProgressForTest.EtaSeconds).IsNull();
  }

  [Test]
  public async Task BothSwitchedOff_NeitherWritesNorReadsTheTallyAsync() {
    var coordinator = new CompletionCoordinator();
    var (worker, metrics) = _build(coordinator,
      new StreamIntegrityOptions { RepairProgressGaugeEnabled = false, BacklogEtaGaugeEnabled = false });
    _count(metrics, opened: 5, closed: 5);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.TallyWrites).IsEmpty().Because("a tally nothing reads is not worth a write per tick");
    await Assert.That(coordinator.TallyReads).IsEmpty();
    await Assert.That(metrics.CurrentRepairProgressForTest.Reported).IsFalse();
  }

  [Test]
  public async Task ATallyThatCannotBeRead_WithdrawsTheProgressAsync() {
    var coordinator = new CompletionCoordinator();
    var (worker, metrics) = _build(coordinator);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    coordinator.Tally = null;
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(metrics.CurrentRepairProgressForTest.Reported).IsFalse()
      .Because("a figure that can no longer be refreshed is stale; no series beats a frozen one");
  }

  [Test]
  public async Task ABacklogThatCannotBeRead_LeavesTheEtaWithoutAValueAsync() {
    var coordinator = new CompletionCoordinator { Backlog = LedgerBacklogSnapshot.NotReported };
    var (worker, metrics) = _build(coordinator);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(metrics.CurrentRepairProgressForTest.ProgressPercent).IsEqualTo(400.0);
    await Assert.That(metrics.CurrentRepairProgressForTest.EtaSeconds).IsNull()
      .Because("an ETA needs the open backlog it divides");
  }
}
