// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Execution policy for a collective-event apply — how the single set-based UPDATE over the cohort is
/// bounded and chunked. A collective apply touches every row in scope in one operation, so left unbounded it
/// holds locks across the whole cohort for the whole duration (a production lock convoy). These knobs keep
/// each statement short and server-bounded.
/// </summary>
/// <remarks>
/// Resolved from DI as the global default; a <c>[CollectiveApplyFor]</c> handler may override per apply (the
/// generated <c>CollectiveApplyEntry</c> carries the overrides). Values flow into the driver adapters, which
/// run each batch in its own transaction: <c>SET LOCAL statement_timeout</c> (the only form that survives
/// PgBouncer transaction pooling) + a keyset <c>LIMIT</c> chunk.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:DispatchAsync_TakesExclusiveAdvisoryLockPerBatchAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:DispatchAsync_WithStatementTimeout_BoundsApplyServerSideAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:DispatchAsync_CohortLargerThanBatchSize_UpdatesAllInMultipleBatchesAsync</tests>
public sealed record CollectiveApplyOptions {
  /// <summary>
  /// Rows mutated per batched UPDATE (keyset <c>LIMIT</c>). Each batch is a short transaction so lock hold
  /// stays brief. Default 1000. Must be positive.
  /// </summary>
  public int BatchSize { get; init; } = 1000;

  /// <summary>
  /// Server-side <c>statement_timeout</c> (via <c>SET LOCAL</c>) applied to each batch transaction, in
  /// seconds. Null leaves the server/role default in place. When set, a runaway batch is canceled by
  /// Postgres itself — so a client timeout can never leave a zombie query running through PgBouncer.
  /// </summary>
  public int? StatementTimeoutSeconds { get; init; }

  /// <summary>
  /// When true (default), each apply batch takes an <em>exclusive</em> <c>pg_advisory_xact_lock</c> keyed on
  /// (table, scope), serializing collective applies that target the same table+scope — across all pods —
  /// instead of letting up to <c>MaxConcurrentPerspectives</c> of them convoy on the same rows. Disjoint
  /// scopes (e.g. different tenants) hash to different keys and still run concurrently.
  /// </summary>
  public bool SerializeApplies { get; init; } = true;

  // NOTE (§7): the btree expression index the apply's WHERE needs — the universal `((scope->>'t'))` tenant
  // envelope — is created at SERVICE STARTUP by the schema generator, not at apply time. An earlier design
  // had an `EnsureIndexes` knob that ran `CREATE INDEX IF NOT EXISTS` inside the apply hot path (taking a
  // SHARE lock on first apply per process); that was removed because index creation must never happen in a
  // live path. Cohort filters correlate by PK so need no extra index.

  /// <summary>
  /// How long a batch waits for the per-scope apply lock before giving up, in seconds.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The lock is held for one batch and released at its commit, so a wait is normally short. Without
  /// a bound the only limit was <see cref="StatementTimeoutSeconds"/>: a contended wait sat for the
  /// full statement timeout, the execution strategy then retried the batch, and the retry joined the
  /// back of the lock queue -- while the work lease, which is renewed only after progress, expired
  /// underneath it. The work was leased again and the wait counted toward dead-lettering, so
  /// contention turned into a reshuffling queue and apply latency of many minutes.
  /// </para>
  /// <para>
  /// Bounded, the wait fails in seconds with a named outcome instead, which a caller can tell apart
  /// from a broken apply. Null restores the old unbounded wait.
  /// </para>
  /// </remarks>
  public int? LockWaitSeconds { get; init; } = 30;

  /// <summary>
  /// How many more times a batch waits for its apply lock after a wait of <see cref="LockWaitSeconds"/> ends
  /// without it, renewing its work lease before each (#964). Zero gives up after the first wait.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A batch waiting behind another keeps its work: after each bounded wait it reports progress through the same
  /// per-batch callback the worker renews the lease from, so the lease outlives the wait and the work is not leased
  /// again underneath it, and the attempt count does not rise. With the default (five more waits of thirty
  /// seconds) a batch waits up to three minutes, about what the unbounded wait used to allow before its statement
  /// timeout, but holding its lease throughout. When every wait is used up the batch gives up with a
  /// <see cref="CollectiveApplyLockBusyException"/> that reports the total wait, which the worker treats as busy,
  /// not failed.
  /// </para>
  /// <para>
  /// An advisory lock has no queue: PostgreSQL wakes its waiters in no guaranteed order, so a waiting batch has no
  /// place in line to keep. Collectives that must apply in order carry an ordering key
  /// (<c>ICollectiveEvent.OrderingKey</c>); those wait in their key's queue, which a busy lock does not reorder.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/messaging/collective-events</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs:LockHeldElsewhere_RenewsTheLeaseWhileItWaits_ThenAppliesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:DispatchAsync_WhileAnotherBatchHoldsTheLock_RenewsTheLeaseAndThenAppliesAsync</tests>
  public int LockWaitRenewals { get; init; } = 5;

  /// <summary>The framework default policy.</summary>
  public static CollectiveApplyOptions Default { get; } = new();

  /// <summary>
  /// The policy for one handler's apply: this policy with the handler's <c>[CollectiveApplyFor]</c>
  /// overrides folded on. An override of <c>0</c> inherits this policy's value.
  /// </summary>
  /// <remarks>
  /// <see cref="SerializeApplies"/> and the lock-wait knobs stay global: exclusive serialization is not a
  /// per-handler choice.
  /// </remarks>
  /// <param name="entry">The handler's apply entry, carrying its overrides.</param>
  /// <returns>The effective policy for that handler's apply.</returns>
  /// <docs>fundamentals/messaging/collective-events</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveApplyOptionsTests.cs</tests>
  public CollectiveApplyOptions For(CollectiveApplyEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return this with {
      BatchSize = entry.BatchSizeOverride > 0 ? entry.BatchSizeOverride : BatchSize,
      StatementTimeoutSeconds = entry.StatementTimeoutSecondsOverride > 0
        ? entry.StatementTimeoutSecondsOverride
        : StatementTimeoutSeconds,
    };
  }
}
