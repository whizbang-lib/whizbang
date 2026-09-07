using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for two <see cref="DisabledSubsystemDiscardPolicy.ShouldDiscard"/> branches the
/// primary suite (<see cref="DisabledSubsystemDiscardTests"/>) never exercises: a disabled
/// gap-detection subsystem, and the audit subsystem's third disjunct (a divergence-detected
/// message, reached only once the first two audit types have already been ruled out). Same
/// livelock this policy exists to remove — a leftover message from a disabled subsystem that
/// never gets recognized as discardable churns on lease-expiry re-claims forever.
/// </summary>
public class DisabledSubsystemDiscardPolicyCoverageTests {

  /// <summary>What breaks: a disabled gap-detection subsystem's leftover
  /// <see cref="IntegrityGapDetected"/> messages have no active handler — without this branch
  /// they'd churn on redelivery exactly like the checkpoint livelock issue #664 fixed.</summary>
  [Test]
  public async Task ShouldDiscard_DisabledGapDetection_DiscardsGapDetectedAsync() {
    var options = new StreamIntegrityOptions { GapDetectionEnabled = false };
    var messageTypeName = TypeNameFormatter.Format(typeof(IntegrityGapDetected));

    await Assert.That(DisabledSubsystemDiscardPolicy.ShouldDiscard(messageTypeName, options)).IsTrue()
      .Because("a disabled subsystem's own message type is noise with no handler once GapDetectionEnabled is off");
  }

  /// <summary>What breaks: the audit disjunct's third check (divergence) is only reached once the
  /// manifest and manifest-request checks both fail — if short-circuit evaluation or the type
  /// match itself regressed, a disabled audit subsystem's divergence messages would keep the
  /// discard from firing.</summary>
  [Test]
  public async Task ShouldDiscard_DisabledAudit_DiscardsDivergenceDetectedAsync() {
    var options = new StreamIntegrityOptions { AuditEnabled = false };
    var messageTypeName = TypeNameFormatter.Format(typeof(IntegrityDivergenceDetected));

    await Assert.That(DisabledSubsystemDiscardPolicy.ShouldDiscard(messageTypeName, options)).IsTrue()
      .Because("divergence is the third of three audit type checks — it must still be reached and matched, not just manifest and manifest-request");
  }
}
