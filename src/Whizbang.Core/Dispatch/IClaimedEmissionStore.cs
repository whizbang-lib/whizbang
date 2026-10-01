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

  /// <summary>
  /// Which of <paramref name="claimKeys"/> are held: claimed and not released.
  /// </summary>
  /// <remarks>
  /// <para>
  /// For a caller that must not act on something another caller already decided, such as the
  /// stranded-saga sweep leaving an abandoned saga alone. A key reads as held for exactly as long as
  /// <see cref="TryClaimAsync"/> would lose on it, so a claim past its expiry that has not been pruned
  /// still reads as held.
  /// </para>
  /// <para>
  /// The default returns <see langword="null"/>: a store written before claims could be read back
  /// cannot tell, and says so rather than answering "none held".
  /// </para>
  /// </remarks>
  /// <param name="claimKeys">The keys to check.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The held subset of <paramref name="claimKeys"/>, or <see langword="null"/> when the store cannot tell.</returns>
  /// <docs>fundamentals/dispatcher/publish-once</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/ClaimedEmissionStoreDefaultsTests.cs:FindClaimed_Default_CannotTellAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Dispatch/ClaimedEmissionStoreTests.cs:FindClaimed_ReturnsExactlyTheHeldKeysAsync</tests>
  Task<IReadOnlySet<string>?> FindClaimedAsync(IReadOnlyCollection<string> claimKeys, CancellationToken cancellationToken)
    => Task.FromResult<IReadOnlySet<string>?>(null);

  /// <summary>
  /// Releases a held claim, so the side effect it gated can happen once more.
  /// </summary>
  /// <remarks>
  /// An operator's act, never part of an ordinary emission: releasing a claim another caller is still
  /// relying on lets its side effect happen twice. The saga framework uses it to re-drive a saga the
  /// watchdog abandoned. The default releases nothing and returns <see langword="false"/>.
  /// </remarks>
  /// <param name="claimKey">The key to release.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="true"/> when a held claim was released.</returns>
  /// <docs>fundamentals/dispatcher/publish-once</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/ClaimedEmissionStoreDefaultsTests.cs:Release_Default_ReleasesNothingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Dispatch/ClaimedEmissionStoreTests.cs:Release_HeldKey_CanBeClaimedAgain_AndAFreeKeyReportsNothingReleasedAsync</tests>
  Task<bool> ReleaseAsync(string claimKey, CancellationToken cancellationToken)
    => Task.FromResult(false);

  /// <summary>
  /// Deletes every claim whose key starts with <paramref name="keyPrefix"/> and that was taken before
  /// <paramref name="claimedBefore"/>.
  /// </summary>
  /// <remarks>
  /// For the owner of a key convention, which alone knows when its claims are spent: the saga
  /// framework prunes its sweep, completion and continuation claims this way, and keeps its
  /// abandonment claims. The prefix is matched literally. The default prunes nothing.
  /// </remarks>
  /// <param name="keyPrefix">The literal key prefix; must not be blank.</param>
  /// <param name="claimedBefore">Only claims taken before this are deleted.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many claims were deleted.</returns>
  /// <docs>fundamentals/dispatcher/publish-once</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/ClaimedEmissionStoreDefaultsTests.cs:Prune_Default_PrunesNothingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Dispatch/ClaimedEmissionStoreTests.cs:Prune_RemovesOnlyThePrefixesClaimsTakenBeforeTheCutoffAsync</tests>
  Task<int> PruneAsync(string keyPrefix, DateTimeOffset claimedBefore, CancellationToken cancellationToken)
    => Task.FromResult(0);
}
