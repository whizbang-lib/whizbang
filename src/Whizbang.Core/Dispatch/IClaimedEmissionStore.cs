// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// state from the event store alone. A claim records an expiry (30 minutes
/// after it is taken in the Postgres driver), and the claimed-emission prune
/// maintenance step deletes it one day after that (<see cref="PruneExpiredAsync"/>).
/// Until it is deleted the claim holds its key.
/// </para>
/// <para>
/// The claim key is opaque to the framework — callers choose any string
/// unique within their domain. A package that owns a key convention can keep
/// its claims out of the general prune with a <see cref="RetainedClaimKeyPrefix"/>
/// and prune them itself (<see cref="PruneAsync"/>): the saga framework does,
/// and never prunes its abandonment claims.
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

  /// <summary>
  /// Deletes up to <paramref name="maxClaims"/> claims whose expiry passed before
  /// <paramref name="expiredBefore"/>, except claims whose key starts with one of
  /// <paramref name="retainedKeyPrefixes"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The general expiry prune, for every claim no owner manages: the claims <c>PublishOnceAsync</c>
  /// takes for a caller's own keys, and the window claims of the framework's maintenance steps. A
  /// retained prefix belongs to an owner that prunes its own claims on its own terms, such as the
  /// saga framework, whose abandonment claims must never expire. Prefixes are matched literally. The
  /// default prunes nothing.
  /// </para>
  /// <para>
  /// Bounded by <paramref name="maxClaims"/> so a table that grew for a long time is drained over
  /// several calls rather than in one long delete.
  /// </para>
  /// </remarks>
  /// <param name="expiredBefore">Only claims whose expiry is before this are deleted.</param>
  /// <param name="retainedKeyPrefixes">Key prefixes whose claims are never deleted by this prune.</param>
  /// <param name="maxClaims">The most claims one call deletes; must be positive.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many claims were deleted.</returns>
  /// <docs>fundamentals/dispatcher/publish-once#claim-expiry</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/ClaimedEmissionStoreDefaultsTests.cs:PruneExpired_Default_PrunesNothingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Dispatch/ClaimedEmissionStoreTests.cs:PruneExpired_RemovesExpiredClaims_AndKeepsRetainedPrefixesAndUnexpiredOnesAsync</tests>
  Task<int> PruneExpiredAsync(
      DateTimeOffset expiredBefore, IReadOnlyCollection<string> retainedKeyPrefixes, int maxClaims, CancellationToken cancellationToken)
    => Task.FromResult(0);
}
