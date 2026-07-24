using System.Text;

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
public static class CollectiveApplyLockKey {
  /// <summary>
  /// The advisory-lock key for a collective apply against <paramref name="table"/> under
  /// <paramref name="scopeKey"/> (a stable identity for the scope — e.g. the serialized scope). Returns a
  /// signed 64-bit value suitable for <c>pg_advisory_xact_lock(bigint)</c>.
  /// </summary>
  public static long Compute(string table, string scopeKey) {
    ArgumentNullException.ThrowIfNull(table);
    ArgumentNullException.ThrowIfNull(scopeKey);
    return _fnv1a64(table + "|" + scopeKey);
  }

  private static long _fnv1a64(string s) {
    const ulong offset = 14695981039346656037UL;
    const ulong prime = 1099511628211UL;
    var hash = offset;
    var bytes = Encoding.UTF8.GetBytes(s);
    foreach (var b in bytes) {
      hash ^= b;
      hash *= prime;
    }
    return unchecked((long)hash);
  }
}
