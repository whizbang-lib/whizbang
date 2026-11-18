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
/// #2349: an origin whose state has not moved since the last full match is not asked. The store decides (the seal, the
/// origin generation, the origin's checkpoint position and the highest position held from it, against the memo of the
/// last full match, and the age of the last ask against <see cref="StreamIntegrityOptions.MaxAskSkipMinutes"/>); the
/// worker asks it once per origin per cycle, skips on <see cref="IntegrityAskDecision.Skip"/>, and counts both.
/// </summary>
/// <docs>resilience/stream-integrity#unchanged-origins-are-not-asked</docs>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
public partial class IntegrityAuditWorkerBranchCoverageTests {

  [Test]
  public async Task Options_MaxAskSkipMinutes_DefaultsToOneHourAsync() {
    await Assert.That(new StreamIntegrityOptions().MaxAskSkipMinutes).IsEqualTo(60)
      .Because("an unchanged origin is still asked at least once an hour, so a local loss with nothing moving is found");
  }

  [Test]
  public async Task AnUnchangedOrigin_IsNotAsked_AndTheCycleStillReachedItAsync() {
    var coordinator = new AuditCoordinator { AskDecision = IntegrityAskDecision.Skip };
    var transport = new CaptureTransport();
    var logger = new MessageLogger();
    var worker = _workerAskingOneOrigin(coordinator, transport, new StreamIntegrityOptions(), logger: logger);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty()
      .Because("nothing about the origin moved since the last full match, so the window above the seal is unchanged");
    await Assert.That(coordinator.AskDecisions.Count).IsEqualTo(1);
    await Assert.That(logger.Saw("none have checkpointed")).IsFalse()
      .Because("a skipped origin was reached; the cycle must not be retried on the startup window as if it asked nobody");
  }

  [Test]
  public async Task TheDecision_IsAskedWithTheAuditsClockAndTheConfiguredBoundAsync() {
    var clock = new FakeTimeProvider(_auditNow);
    var coordinator = new AuditCoordinator();
    var worker = _workerAskingOneOrigin(coordinator, new CaptureTransport(), new StreamIntegrityOptions { MaxAskSkipMinutes = 45 },
      clock: clock);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var (_, now, maxSkip) = coordinator.AskDecisions.Single();
    await Assert.That(now).IsEqualTo(_auditNow).Because("the last ask is stamped on the audit's own clock");
    await Assert.That(maxSkip).IsEqualTo(TimeSpan.FromMinutes(45));
  }

  [Test]
  [Arguments(IntegrityAskDecision.NothingRemembered, "nothing_remembered")]
  [Arguments(IntegrityAskDecision.Moved, "moved")]
  [Arguments(IntegrityAskDecision.BoundReached, "bound_reached")]
  [NotInParallel("Metrics")]
  public async Task EveryOtherDecision_Asks_AndTagsTheAskWithItsReasonAsync(IntegrityAskDecision decision, string reason) {
    using var factory = new TestMeterFactory();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var coordinator = new AuditCoordinator { AskDecision = decision };
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, new StreamIntegrityOptions(), metrics);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(_onlyRequest(transport).Windowed).IsTrue();
    await Assert.That(_sumTagged(helper, "whizbang.stream_integrity.manifests_requested", StreamIntegrityMetrics.ASK_REASON_TAG, reason))
      .IsEqualTo(1d);
    await Assert.That(_sum(helper, "whizbang.stream_integrity.manifest_asks_skipped")).IsEqualTo(0d);
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task ASkippedAsk_IsCountedByOriginAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var coordinator = new AuditCoordinator { AskDecision = IntegrityAskDecision.Skip };
    var worker = _workerAskingOneOrigin(coordinator, new CaptureTransport(), new StreamIntegrityOptions(), metrics);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(_sumTagged(helper, "whizbang.stream_integrity.manifest_asks_skipped", "origin", "origin-a")).IsEqualTo(1d);
    await Assert.That(_sum(helper, "whizbang.stream_integrity.manifests_requested")).IsEqualTo(0d);
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task ABoundOfZero_AsksEveryCycle_WithoutConsultingTheMemoAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var coordinator = new AuditCoordinator { AskDecision = IntegrityAskDecision.Skip };
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, new StreamIntegrityOptions { MaxAskSkipMinutes = 0 }, metrics);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.AskDecisions).IsEmpty().Because("0 turns the skip off: every cycle asks, as before #2349");
    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(_sumTagged(helper, "whizbang.stream_integrity.manifests_requested", StreamIntegrityMetrics.ASK_REASON_TAG, "every_cycle"))
      .IsEqualTo(1d);
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task TheFullHistoryCompare_AlwaysAsks_WithoutConsultingTheMemoAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var coordinator = new AuditCoordinator { AskDecision = IntegrityAskDecision.Skip };
    var transport = new CaptureTransport();
    var worker = _workerAskingOneOrigin(coordinator, transport, new StreamIntegrityOptions { FullHistoryCompare = { Enabled = true } },
      metrics);

    await worker.RunCheckOnceAsync(IntegrityCheck.FullHistoryCompare, CancellationToken.None);

    await Assert.That(coordinator.AskDecisions).IsEmpty()
      .Because("the compare re-verifies history below the seal; an unchanged open window says nothing about it");
    await Assert.That(_onlyRequest(transport).UseRecompute).IsTrue();
    await Assert.That(_sumTagged(helper, "whizbang.stream_integrity.manifests_requested", StreamIntegrityMetrics.ASK_REASON_TAG, "sweep"))
      .IsEqualTo(1d);
  }
}
