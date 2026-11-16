// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2353: the full-history compare and the sealed-history re-check each run only when their own section enables them,
/// each on its own schedule. Both are off by default, and turning them off never stops the routine windowed audit.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
public partial class IntegrityAuditWorkerBranchCoverageTests {

  private static StreamIntegrityOptions _bothChecksOn() => new() {
    RepairMode = IntegrityRepairMode.ReportOnly,
    FullHistoryCompare = { Enabled = true },
    SealRecheck = { Enabled = true },
  };

  /// <summary>A worker with one origin to ask, so each cycle's manifest request can be read back.</summary>
  private static IntegrityAuditWorker _workerAskingOneOrigin(
      AuditCoordinator coordinator, CaptureTransport transport, StreamIntegrityOptions options,
      StreamIntegrityMetrics? metrics = null, FakeTimeProvider? clock = null,
      IntegritySweepScheduleState? scheduleState = null) {
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(TrackedGuid.New().Value, "origin-a", clock?.GetUtcNow() ?? _auditNow, "origin-a.requests");
    return _buildWorker(coordinator, new CaptureDispatcher(), transport, options, tracker,
      metrics: metrics, clock: clock, scheduleState: scheduleState);
  }

  private static RequestIntegrityManifest _onlyRequest(CaptureTransport transport) =>
    _deserializeRequest(transport.Published.Single().Envelope);

  // ── off by default: the counter cycle runs neither check, and the audit stays windowed ──────

  [Test]
  public async Task ACounterCycle_WithTheDefaults_RunsNeitherCheck_AndStillAsksWindowedAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, new StreamIntegrityOptions { FullSweepEveryNthAudit = 1 });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls).IsEqualTo(0).Because("the full-history compare is off by default");
    await Assert.That(coordinator.EpochVerifyCalls).IsEqualTo(0).Because("the seal re-check is off by default");
    var request = _onlyRequest(transport);
    await Assert.That(request.Windowed).IsTrue()
      .Because("turning the re-checks off never stops the routine windowed audit");
    await Assert.That(request.UseRecompute).IsFalse();
  }

  [Test]
  public async Task ACounterCycle_WithTheFullHistoryCompareOn_HealsASliceAndAsksFromTheLookbackAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, new StreamIntegrityOptions {
      FullSweepEveryNthAudit = 1,
      FullHistoryCompare = { Enabled = true },
    });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls).IsEqualTo(1);
    await Assert.That(coordinator.EpochVerifyCalls).IsEqualTo(0).Because("the re-check has its own switch");
    var request = _onlyRequest(transport);
    await Assert.That(request.Windowed).IsTrue().Because("the compare asks from its lookback, windowed (#2361)");
    await Assert.That(request.UseRecompute).IsTrue();
    await Assert.That(request.SinceSequence).IsEqualTo(0L).Because("nothing is sealed, so the lookback reaches the start");
  }

  [Test]
  public async Task ACounterCycle_WithTheSealRecheckOn_RechecksWithTheConfiguredLimits_AndStaysWindowedAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var options = new StreamIntegrityOptions {
      FullSweepEveryNthAudit = 1,
      AuditSettleWindowMinutes = 30,
      SealRecheck = { Enabled = true, MaxEpochsPerRun = 77, OnDrift = SealDriftAction.ReportOnly },
    };
    var worker = _workerAskingOneOrigin(coordinator, transport, options);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls).IsEqualTo(0);
    await Assert.That(coordinator.RecheckRequests.Single())
      .IsEqualTo(SealRecheckRequest.From(options.SealRecheck, TimeSpan.FromMinutes(30)));
    await Assert.That(_onlyRequest(transport).Windowed).IsTrue();
  }

  [Test]
  public async Task ACounterCycle_LeavesACheckToItsSchedule_OnceTheScheduleIsRegisteredAsync() {
    var coordinator = new AuditCoordinator();
    var worker = _workerAskingOneOrigin(coordinator, new CaptureTransport(),
      _withCounterEveryCycle(_bothChecksOn()),
      scheduleState: new IntegritySweepScheduleState { CronActive = true, SealRecheckCronActive = true });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls).IsEqualTo(0);
    await Assert.That(coordinator.EpochVerifyCalls).IsEqualTo(0)
      .Because("a check its schedule owns would otherwise run on both cadences");
  }

  [Test]
  public async Task ACounterCycle_RunsEachCheckWhoseScheduleIsNotRegistered_AndOnlyThatOneAsync() {
    var coordinator = new AuditCoordinator();
    var worker = _workerAskingOneOrigin(coordinator, new CaptureTransport(),
      _withCounterEveryCycle(_bothChecksOn()),
      scheduleState: new IntegritySweepScheduleState { CronActive = true, SealRecheckCronActive = false });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls).IsEqualTo(0);
    await Assert.That(coordinator.EpochVerifyCalls).IsEqualTo(1);
  }

  private static StreamIntegrityOptions _withCounterEveryCycle(StreamIntegrityOptions options) {
    options.FullSweepEveryNthAudit = 1;
    return options;
  }

  // ── a scheduled run names its check ────────────────────────────────────────────────────────

  [Test]
  public async Task RunCheckOnce_TheSealRecheck_RechecksOnly_AndAsksNoOriginAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, _bothChecksOn());

    await worker.RunCheckOnceAsync(IntegrityCheck.SealRecheck, CancellationToken.None);

    await Assert.That(coordinator.EpochVerifyCalls).IsEqualTo(1);
    await Assert.That(coordinator.VerifyCalls).IsEqualTo(0);
    await Assert.That(transport.Published).IsEmpty()
      .Because("the re-check is local: it recomputes this service's own seals and asks nobody");
  }

  [Test]
  public async Task RunCheckOnce_TheFullHistoryCompare_HealsASliceAndAsksFromTheLookbackAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, _bothChecksOn());

    await worker.RunCheckOnceAsync(IntegrityCheck.FullHistoryCompare, CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls).IsEqualTo(1);
    await Assert.That(coordinator.EpochVerifyCalls).IsEqualTo(0);
    await Assert.That(_onlyRequest(transport).Windowed).IsTrue();
  }

  [Test]
  [Arguments(IntegrityCheck.SealRecheck)]
  [Arguments(IntegrityCheck.FullHistoryCompare)]
  public async Task RunCheckOnce_ADisabledCheck_RunsNothingAtAllAsync(IntegrityCheck check) {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, new StreamIntegrityOptions());

    await worker.RunCheckOnceAsync(check, CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls + coordinator.EpochVerifyCalls).IsEqualTo(0);
    await Assert.That(transport.Published).IsEmpty()
      .Because("a schedule left behind for a check turned off must not run an extra audit cycle either");
  }

  [Test]
  public async Task RunCheckOnce_ACheckThisBuildDoesNotKnow_RunsNothingAtAllAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, _bothChecksOn());

    await worker.RunCheckOnceAsync((IntegrityCheck)99, CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls + coordinator.EpochVerifyCalls).IsEqualTo(0)
      .Because("an occurrence naming a check this build does not know (one written by a newer release) runs nothing");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task RunSweepOnce_WithBothChecksOff_RunsNothingAtAllAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, new StreamIntegrityOptions());

    await worker.RunSweepOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls + coordinator.EpochVerifyCalls).IsEqualTo(0);
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task RunSweepOnce_WithOnlyTheSealRecheckOn_RechecksWithoutAnAuditCycleAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, new StreamIntegrityOptions { SealRecheck = { Enabled = true } });

    await worker.RunSweepOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.EpochVerifyCalls).IsEqualTo(1);
    await Assert.That(coordinator.VerifyCalls).IsEqualTo(0);
    await Assert.That(transport.Published).IsEmpty();
  }

  // ── what each check reports ───────────────────────────────────────────────────────────────

  [Test]
  [NotInParallel("Metrics")]
  public async Task TheSealRecheck_CountsEpochsAndDrift_AndTimesTheRun_WithNoStreamOrTenantTagsAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var clock = new FakeTimeProvider(_auditNow);
    var coordinator = new AuditCoordinator {
      EpochResult = new EpochVerificationResult(9, 2),
      DuringRecheck = () => clock.Advance(TimeSpan.FromSeconds(3)),
    };
    var worker = _workerAskingOneOrigin(coordinator, new CaptureTransport(),
      new StreamIntegrityOptions { SealRecheck = { Enabled = true, OnDrift = SealDriftAction.ReportOnly } },
      metrics, clock);

    await worker.RunCheckOnceAsync(IntegrityCheck.SealRecheck, CancellationToken.None);

    await Assert.That(_sumTagged(helper, "whizbang.stream_integrity.epochs_rechecked", "check", "seal_recheck")).IsEqualTo(9d);
    await Assert.That(_sumTagged(helper, "whizbang.stream_integrity.check_drift_found", "check", "seal_recheck")).IsEqualTo(2d);
    await Assert.That(_sumTagged(helper, "whizbang.stream_integrity.check_duration", "check", "seal_recheck")).IsEqualTo(3d);
    await Assert.That(_sum(helper, "whizbang.stream_integrity.digest_drift_healed", "epoch-refolded")).IsEqualTo(0d)
      .Because("report-only refolds nothing, so nothing is counted as healed");
    await Assert.That(helper.GetByName("whizbang.stream_integrity.check_drift_found")
        .All(m => m.Tags.Keys.All(k => k == "check"))).IsTrue()
      .Because("the plan forbids per-stream and per-tenant attributes; the check is the only tag");
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task TheFullHistoryCompare_CountsDrift_AndTimesTheRunAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var clock = new FakeTimeProvider(_auditNow);
    var coordinator = new AuditCoordinator {
      DigestResult = new DigestVerificationResult { BucketsChecked = 7, DriftUpdated = 2, DriftRemoved = 1, DriftAdded = 0 },
      DuringHeal = () => clock.Advance(TimeSpan.FromSeconds(5)),
    };
    var worker = _workerAskingOneOrigin(coordinator, new CaptureTransport(),
      new StreamIntegrityOptions { FullHistoryCompare = { Enabled = true } }, metrics, clock);

    await worker.RunCheckOnceAsync(IntegrityCheck.FullHistoryCompare, CancellationToken.None);

    await Assert.That(_sumTagged(helper, "whizbang.stream_integrity.check_drift_found", "check", "full_history_compare")).IsEqualTo(3d);
    await Assert.That(_sumTagged(helper, "whizbang.stream_integrity.check_duration", "check", "full_history_compare")).IsEqualTo(5d);
  }

  [Test]
  public async Task TheSealRecheck_WithoutMetrics_StillRunsAndLogsAsync() {
    var coordinator = new AuditCoordinator { EpochResult = new EpochVerificationResult(4, 1) };
    var logger = new MessageLogger();
    var worker = _buildWorker(coordinator, new CaptureDispatcher(), new CaptureTransport(),
      new StreamIntegrityOptions { SealRecheck = { Enabled = true, OnDrift = SealDriftAction.ReportOnly } }, logger: logger);

    await worker.RunCheckOnceAsync(IntegrityCheck.SealRecheck, CancellationToken.None);

    await Assert.That(coordinator.EpochVerifyCalls).IsEqualTo(1);
    await Assert.That(logger.Saw("left as sealed")).IsTrue()
      .Because("a report-only drift says the seal was not touched, so an operator knows to look");
  }

  private static double _sumTagged(MetricAssertionHelper helper, string instrument, string tag, string value) =>
    helper.GetByName(instrument)
      .Where(m => m.Tags.TryGetValue(tag, out var v) && v == value)
      .Sum(m => m.Value);
}
