// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Regression locks for the stream-integrity out-of-the-box posture. Detection is ON by default and
/// repair is <see cref="IntegrityRepairMode.ReportOnly"/> BY DEFAULT: report and let an operator decide;
/// <see cref="IntegrityRepairMode.AutoRepairCapped"/> (storm caps bound every rung) is the explicit opt-IN. Changing any default silently changes what
/// every consumer's production system does — lock them.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityCheckpoint.cs</code-under-test>
public class StreamIntegrityOptionsDefaultsTests {

  [Test]
  public async Task SafeDefault_RepairModeIsReportOnlyAsync() {
    var options = new StreamIntegrityOptions();

    await Assert.That(options.RepairMode).IsEqualTo(IntegrityRepairMode.ReportOnly)
      .Because("the out-of-the-box posture is REPORT-ONLY: detection on, repair only when an operator opts in; a default that mutates data unasked is not a trustworthy default.");
    await Assert.That(options.CheckpointsEnabled).IsTrue();
    await Assert.That(options.GapDetectionEnabled).IsTrue();
    await Assert.That(options.AuditEnabled).IsTrue();
    await Assert.That(options.BackfillOnSubscriptionGrowth).IsTrue();
    await Assert.That(options.AuditOnStartup).IsTrue()
      .Because("historical divergence must heal shortly after deploy, not a full interval later — A1c made startup audits cheap (O(types) wire).");
    await Assert.That(options.StartupAuditMaxJitterSeconds).IsEqualTo(300)
      .Because("the jitter splay de-synchronizes a fleet deploy so startup audits never storm.");
  }

  [Test]
  public async Task Defaults_TheDerivedCompletionGaugesAreOn_OverAnHourAsync() {
    var options = new StreamIntegrityOptions();

    await Assert.That(options.VerifiedPercentGaugeEnabled).IsTrue()
      .Because("#2261: a dashboard answers \"are we done?\" without configuration; a team that prunes its feed switches them off");
    await Assert.That(options.RepairProgressGaugeEnabled).IsTrue();
    await Assert.That(options.BacklogEtaGaugeEnabled).IsTrue();
    await Assert.That(options.RepairProgressWindowMinutes).IsEqualTo(60);
  }

  [Test]
  public async Task Defaults_StormCapsBoundEveryRepairRungAsync() {
    var options = new StreamIntegrityOptions();

    await Assert.That(options.MaxAutoRepairRequestsPerCheckpoint).IsEqualTo(10)
      .Because("auto-repair by default is only safe because every rung is hard-capped.");
    await Assert.That(options.MaxAutoRepairRequestsPerAudit).IsEqualTo(25);
    await Assert.That(options.MaxRepairDrainBacklog).IsEqualTo(10_000)
      .Because("#1792: the paced drain's ledger needs a total bound, or it grows with every divergent stream across audits.");
    await Assert.That(options.MaxAutoRebuildsPerAudit).IsEqualTo(5);
    await Assert.That(options.MaxCoverageGapReportsPerAudit).IsEqualTo(100);
    await Assert.That(options.MaxDrillDownTypesPerAudit).IsEqualTo(10);
    await Assert.That(options.FullSweepEveryNthAudit).IsEqualTo(7);
  }
  /// <summary>
  /// #2001: integrity deliveries wait in a queue for the maintainer's drain. These defaults decide how quickly a
  /// received checkpoint or redelivery request is acted on, how long one pass may hold the integrity slot, and when a
  /// delivery that keeps failing is given up.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-delivery-options</docs>
  [Test]
  public async Task Defaults_TheIntegrityDeliveryDrainIsPromptBoundedAndGivesUpAsync() {
    var options = new StreamIntegrityOptions();

    await Assert.That(options.IntegrityDeliveryIntervalSeconds).IsEqualTo(15)
      .Because("a queued checkpoint must be read well inside the checkpoint interval, or liveness reads late");
    await Assert.That(options.IntegrityDeliveryBatchSize).IsEqualTo(50);
    await Assert.That(options.MaxIntegrityDeliveriesPerPass).IsEqualTo(500)
      .Because("one pass holds the integrity slot, which cleanup waits behind; the bound keeps a backlog from holding it indefinitely");
    await Assert.That(options.IntegrityDeliveryLeaseSeconds).IsEqualTo(300)
      .Because("a leased delivery is hidden from a second holder during a duty failover for at most this long");
    await Assert.That(options.MaxIntegrityDeliveryAttempts).IsEqualTo(5);
    await Assert.That(options.IntegrityDeliveryRetryBackoffSeconds).IsEqualTo(30);
  }
}
