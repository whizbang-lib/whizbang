namespace Whizbang.Core.Lifecycle;

/// <summary>
/// What the maintenance reaper does when a <c>PreDestruction</c> hook keeps failing for an ephemeral batch —
/// the failure-policy rung of the ephemeral override ladder (framework default → global option → later a
/// per-type <c>[Ephemeral(OnDestroyFailure)]</c> attribute → named policy → programmable hook).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public enum OnDestroyFailure {
  /// <summary>
  /// Default. Retry with a TTL-halving backoff up to <c>MaxDestructionRetries</c>, then FORCE-delete the batch
  /// (the reaper deletes it despite the failing hook) — a permanently-broken hook can never leak storage.
  /// </summary>
  RetryThenForcedDelete = 0,

  /// <summary>
  /// Retry the same way, but past the cap KEEP the data (hold far-future) instead of force-deleting. The
  /// developer's explicit leak-risk choice — e.g. a compaction/archive whose summary must never be lost even if
  /// the hook is broken. Observable via the destruction-hold table.
  /// </summary>
  RetryThenKeep = 1,

  /// <summary>
  /// Do not retry — force-delete on the first failure (the pre-E2-5 fail-open behaviour). For destruction work
  /// that is cheap/idempotent and whose loss on failure is acceptable.
  /// </summary>
  ForceDeleteImmediately = 2,
}
