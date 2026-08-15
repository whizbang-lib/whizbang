namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// Computes the stable 64-bit advisory-lock key for a collective apply, keyed on the target
/// <c>(table, scope)</c>. Both the collective apply (which takes the key <em>exclusively</em>) and — where
/// wired — the standard per-row apply (which takes it <em>shared</em>) must derive the identical key for the
/// same table+scope, so a process-stable hash (FNV-1a 64) is used rather than <c>string.GetHashCode</c>
/// (randomized per process) or Postgres <c>hashtext</c> (undocumented stability).
/// </summary>
/// <remarks>
/// The key feeds <c>pg_advisory_xact_lock(key)</c> / <c>pg_advisory_xact_lock_shared(key)</c> — transaction
/// scoped, so it releases at each batch commit (brief holds), and Postgres's own deadlock detector covers it.
/// Scope granularity means disjoint scopes (different tenants) get different keys and run concurrently.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:DispatchAsync_TakesExclusiveAdvisoryLockPerBatchAsync</tests>
public static class CollectiveApplyLockKey {
  /// <summary>
  /// The advisory-lock key for a collective apply against <paramref name="table"/> under
  /// <paramref name="scopeKey"/> (a stable identity for the scope — e.g. the serialized scope). Returns a
  /// signed 64-bit value suitable for <c>pg_advisory_xact_lock(bigint)</c>.
  /// </summary>
  public static long Compute(string table, string scopeKey) {
    ArgumentNullException.ThrowIfNull(table);
    ArgumentNullException.ThrowIfNull(scopeKey);
    return Fnv1a64.Compute(table + "|" + scopeKey);
  }
}
