namespace Whizbang.Core.Perspectives;

/// <summary>
/// Execution policy for a collective-event apply — how the single set-based UPDATE over the cohort is
/// bounded and chunked. A collective apply touches every row in scope in one operation, so left unbounded it
/// holds locks across the whole cohort for the whole duration (the production lock convoy). These knobs keep
/// each statement short and server-bounded.
/// </summary>
/// <remarks>
/// Resolved from DI as the global default; a <c>[CollectiveApplyFor]</c> handler may override per apply (the
/// generated <c>CollectiveApplyEntry</c> carries the overrides). Values flow into the driver adapters, which
/// run each batch in its own transaction: <c>SET LOCAL statement_timeout</c> (the only form that survives
/// PgBouncer transaction pooling) + a keyset <c>LIMIT</c> chunk.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public sealed record CollectiveApplyOptions {
  /// <summary>
  /// Rows mutated per batched UPDATE (keyset <c>LIMIT</c>). Each batch is a short transaction so lock hold
  /// stays brief. Default 1000. Must be positive.
  /// </summary>
  public int BatchSize { get; init; } = 1000;

  /// <summary>
  /// Server-side <c>statement_timeout</c> (via <c>SET LOCAL</c>) applied to each batch transaction, in
  /// seconds. Null leaves the server/role default in place. When set, a runaway batch is cancelled by
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

  /// <summary>The framework default policy.</summary>
  public static CollectiveApplyOptions Default { get; } = new();
}
