using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Layer 2 quarantines a message when it has been "durably observed" more times than the bound
/// allows. The counter it reads records DELIVERIES, so a message may cross the bound without a
/// receptor ever having tried it — and quarantining then destroys a message that never failed.
/// </summary>
/// <remarks>
/// <para>
/// The option's own documentation states the intended semantics: "Ten independent observations of
/// the same message id — each one a delivery this service recorded AND THEN FAILED TO SETTLE — is a
/// loop, not a retry." The store increments on every already-seen delivery instead, settled or not,
/// so legitimate broadcast fan-out counts as failure. A message published to more subscriptions than
/// the bound is therefore guaranteed to be quarantined on a perfectly healthy system.
/// </para>
/// <para>
/// Observed in production: hundreds of rows quarantined with an attempt count of ZERO — dead-lettered
/// having never been processed — clustered exactly one past the bound, which is the signature of
/// trip-on-crossing rather than of a genuine retry loop.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
[Category("Routing")]
public class PoisonObservationRequiresProcessingEvidenceTests {

  private static PoisonMessageDetector _detector(int bound = 10) =>
    new(Options.Create(new PoisonMessageOptions { MaxDurableObservations = bound }),
        NullLogger<PoisonMessageDetector>.Instance,
        new System.Diagnostics.Metrics.Meter("Whizbang.Core.Tests.PoisonObservationEvidence"));

  private static PoisonEvaluationContext _ctx(int observations, int? attempts) =>
    new(MessageId: Guid.CreateVersion7().ToString(),
        FirstEnqueuedAt: null,
        BrokerDeliveryCount: null,
        DurableObservationCount: observations,
        Now: DateTimeOffset.UtcNow) { ProcessingAttempts = attempts };

  [Test]
  public async Task NeverAttempted_IsNotQuarantined_EvenPastTheObservationBoundAsync() {
    // Fan-out to more subscriptions than the bound: delivered many times, tried zero times.
    var verdict = _detector().Evaluate(_ctx(observations: 16, attempts: 0));

    await Assert.That(verdict.ShouldQuarantine).IsFalse()
      .Because("a message no receptor has ever attempted cannot be 'a redelivery loop making no "
             + "progress' — quarantining it destroys a message that never failed, which is how "
             + "broadcast traffic was silently dead-lettered on a healthy system");
  }

  [Test]
  public async Task AttemptedAndStillLooping_IsStillQuarantinedAsync() {
    // The guard must keep doing its job. This is the case it exists for: the message HAS been
    // processed, repeatedly, and is still coming back.
    var verdict = _detector().Evaluate(_ctx(observations: 16, attempts: 12));

    await Assert.That(verdict.ShouldQuarantine).IsTrue()
      .Because("a message that has been attempted and keeps returning IS a loop — relaxing the "
             + "guard for never-attempted messages must not disarm it for genuinely poisoned ones");
    await Assert.That(verdict.Reason).IsEqualTo(PoisonQuarantineReason.ObservationCountExceeded);
  }

  [Test]
  public async Task UnknownAttemptCount_DoesNotQuarantine_BecauseAbsenceIsNotEvidenceAsync() {
    // A transport that cannot report attempts must not have its messages destroyed by default.
    // Null means "not measured", and quarantining on an unmeasured signal is the same silent-harm
    // pattern as treating an unmeasured drain rate as zero.
    var verdict = _detector().Evaluate(_ctx(observations: 16, attempts: null));

    await Assert.That(verdict.ShouldQuarantine).IsFalse()
      .Because("null attempts means UNMEASURED, not zero-failures-so-far — destroying a message on "
             + "the strength of a reading nobody took is the dangerous direction to be wrong in");
  }

  [Test]
  public async Task BelowTheBound_IsNeverQuarantined_RegardlessOfAttemptsAsync() {
    var verdict = _detector().Evaluate(_ctx(observations: 3, attempts: 99));

    await Assert.That(verdict.ShouldQuarantine).IsFalse()
      .Because("the bound is the bound — attempts are an additional precondition for quarantine, "
             + "not an alternative trigger that could fire below it");
  }
}
