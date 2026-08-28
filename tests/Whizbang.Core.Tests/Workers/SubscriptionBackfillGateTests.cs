using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Subscription-growth backfill must answer to <c>RepairMode</c>, like every other repair emitter.
/// </summary>
/// <remarks>
/// <para>
/// A consumed type that is absent from the persisted registry is treated as subscription growth:
/// history exists this service never received, repaired by broadcasting one request that every
/// origin answers with its own history. That is correct for a genuinely new subscription and
/// catastrophic for the case that actually triggers it most often — a package upgrade, which
/// changes the framework's consumed-type catalog on every service at once.
/// </para>
/// <para>
/// Every other redelivery emitter is gated on <c>RepairMode</c>; this one was gated only on its own
/// flag. So the documented way to stop a service emitting repair traffic — the setting the
/// diagnostics themselves recommend — left this path running, and an operator following that advice
/// during an incident watched the traffic continue.
/// </para>
/// <para>
/// Observed: a version bump produced a self-sustaining replay that reached tens of thousands of
/// queued broker messages against stores holding millions of events. Setting ReportOnly everywhere
/// changed nothing; only the separate backfill flag stopped it.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/SubscriptionBackfillGate.cs</code-under-test>
[Category("Workers")]
public class SubscriptionBackfillGateTests {

  [Test]
  public async Task ReportOnlySuppressesBackfillAsync() {
    var allowed = SubscriptionBackfillGate.ShouldRequestBackfill(
      backfillOnSubscriptionGrowth: true, repairMode: IntegrityRepairMode.ReportOnly);

    await Assert.That(allowed).IsFalse()
      .Because("ReportOnly is the documented way to stop a service emitting repair traffic, and the "
             + "diagnostics recommend it by name — a path that ignores it makes the advice wrong "
             + "at exactly the moment someone follows it");
  }

  [Test]
  public async Task AutoRepairWithBackfillEnabledAllowsItAsync() {
    var allowed = SubscriptionBackfillGate.ShouldRequestBackfill(
      backfillOnSubscriptionGrowth: true, repairMode: IntegrityRepairMode.AutoRepairCapped);

    await Assert.That(allowed).IsTrue()
      .Because("a genuinely new subscription on a self-healing deployment must still backfill, or "
             + "the gate has traded a storm for silent divergence");
  }

  [Test]
  public async Task TheBackfillFlagStillDisablesItUnderAutoRepairAsync() {
    var allowed = SubscriptionBackfillGate.ShouldRequestBackfill(
      backfillOnSubscriptionGrowth: false, repairMode: IntegrityRepairMode.AutoRepairCapped);

    await Assert.That(allowed).IsFalse()
      .Because("the existing opt-out must keep working — this change adds a second gate, it does "
             + "not replace the first");
  }

  [Test]
  public async Task BothDisabledIsStillDisabledAsync() {
    var allowed = SubscriptionBackfillGate.ShouldRequestBackfill(
      backfillOnSubscriptionGrowth: false, repairMode: IntegrityRepairMode.ReportOnly);

    await Assert.That(allowed).IsFalse();
  }

  [Test]
  public async Task EitherGateAloneIsEnoughToStopItAsync() {
    // The property that matters operationally: an operator reaching for EITHER control gets relief,
    // without having to know both exist.
    foreach (var (backfill, mode) in new[] {
      (true, IntegrityRepairMode.ReportOnly),
      (false, IntegrityRepairMode.AutoRepairCapped),
    }) {
      await Assert.That(SubscriptionBackfillGate.ShouldRequestBackfill(backfill, mode)).IsFalse()
        .Because("requiring an operator to find two unrelated switches to stop one behavior is how "
               + "an incident lasts hours longer than it needs to");
    }
  }
}
