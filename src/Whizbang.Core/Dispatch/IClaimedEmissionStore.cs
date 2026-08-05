namespace Whizbang.Core.Dispatch;

/// <summary>
/// Atomic claim primitive for once-per-key emission. Backs
/// <c>IDispatcher.PublishOnceAsync</c> and any caller that needs to gate a
/// side effect on "first writer wins" semantics under concurrent receptor
/// or command-handler scopes.
/// </summary>
/// <remarks>
/// <para>
/// The claim store is messaging infrastructure, not domain state. Claim
/// rows do not participate in projection replay; rebuilds reconstruct
/// state from the event store alone. Claims expire on a wide safety
/// margin (30 minutes by default in the Postgres driver) so the rare
/// "claim taken, emission crashed" case eventually self-heals when the
/// retry path re-attempts the operation.
/// </para>
/// <para>
/// The claim key is opaque to the framework — callers choose any string
/// unique within their domain. Convention for sagas (see
/// <c>Whizbang.Sagas.SagaCompletionGuard</c>) is to use the saga id as
/// the key, which is unique-per-saga-completion because each saga emits
/// exactly one terminal completion event.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/publish-once</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPublishOnceTests.cs:PublishOnceAsync_DistinctKeys_BothReturnTrueAndBothFireAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPublishOnceTests.cs:PublishOnceAsync_EmitsClaimsWonMetricOnWinAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPublishOnceTests.cs:PublishOnceAsync_EmitsClaimsLostMetricOnLossAsync</tests>
public interface IClaimedEmissionStore {

  /// <summary>
  /// Attempt to claim the given key. Returns <c>true</c> if the claim was
  /// taken by this caller (proceed with the side effect); <c>false</c> if
  /// the key was already claimed by another caller (intentional no-op).
  /// </summary>
  /// <param name="claimKey">
  /// Caller-chosen key unique within the caller's domain. Opaque to the
  /// framework.
  /// </param>
  /// <param name="claimedByEventId">
  /// MessageId of the event whose emission this claim guards. Audit-only;
  /// the framework does not read this value back.
  /// </param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// <c>true</c> if the caller now owns the claim and should proceed with
  /// the gated side effect; <c>false</c> if another caller already owns
  /// it and the side effect should be skipped.
  /// </returns>
  /// <remarks>
  /// Atomicity is enforced at the storage layer (e.g. PostgreSQL
  /// <c>INSERT … ON CONFLICT DO NOTHING</c>). When the caller is inside
  /// an ambient transaction the claim INSERT participates in that
  /// transaction, so a rollback of the outer scope releases the claim —
  /// the invariant the framework relies on is <em>claim taken iff
  /// emission committed</em>.
  /// </remarks>
  Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken);
}
