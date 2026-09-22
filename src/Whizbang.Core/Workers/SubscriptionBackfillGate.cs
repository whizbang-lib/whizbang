using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Decides whether observed subscription growth should be repaired by requesting history.
/// </summary>
/// <remarks>
/// <para>
/// A consumed event type absent from the persisted registry means history exists that this service
/// never received. The repair is one broadcast request that every origin answers with its own
/// history. For a genuinely new subscription that is correct and cheap relative to the divergence
/// it closes.
/// </para>
/// <para>
/// The trigger is not always a new subscription, though. Upgrading the framework changes the
/// consumed-type catalog, so a version bump alone can look like growth on every service at once —
/// each broadcasting for full history from every origin, against stores that may hold millions of
/// events.
/// </para>
/// <para>
/// Two independent controls therefore gate it, and EITHER stops it. <c>RepairMode</c> is the
/// setting operators already reach for to stop a service emitting repair traffic — every other
/// emitter honors it, the diagnostics recommend it by name, and a path that ignored it made that
/// advice wrong at exactly the moment someone followed it during an incident. The dedicated
/// backfill flag remains for deployments that want self-healing generally but not this repair.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/SubscriptionBackfillGateTests.cs</tests>
public static class SubscriptionBackfillGate {

  /// <summary>Decides whether to broadcast a history request for grown subscriptions.</summary>
  /// <param name="backfillOnSubscriptionGrowth">The dedicated opt-out for this repair.</param>
  /// <param name="repairMode">The service-wide repair posture.</param>
  /// <returns>True only when both controls permit it.</returns>
  public static bool ShouldRequestBackfill(
      bool backfillOnSubscriptionGrowth, IntegrityRepairMode repairMode)
    // Deliberately an AND: an operator reaching for either control gets relief without having to
    // know the other exists. Requiring both to be found is how an incident runs long.
    => backfillOnSubscriptionGrowth && repairMode == IntegrityRepairMode.AutoRepairCapped;
}
