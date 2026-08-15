namespace Whizbang.Data.Postgres;

/// <summary>
/// Computes the advisory-lock key that serializes Whizbang schema initialization for one schema,
/// so exactly one instance runs migrations while the rest wait.
/// </summary>
/// <remarks>
/// <para>
/// The key must be identical in every process initializing the same schema — that is the whole
/// point of the lock. It is therefore derived from a process-stable hash (FNV-1a 64, shared with
/// <see cref="Collective.CollectiveApplyLockKey"/>) rather than <see cref="string.GetHashCode()"/>,
/// whose seed .NET randomizes per process. With a per-process key every instance acquires its own
/// private lock, all of them enter the migration path together, and the only things standing
/// between that and concurrent DDL are the in-lock hash re-check and <c>IF NOT EXISTS</c> — neither
/// of which covers <c>CREATE OR REPLACE FUNCTION</c> or settings-gated data migrations.
/// </para>
/// <para>
/// The hashed input is namespaced so the key cannot collide with the other advisory-lock families
/// sharing Postgres's single-bigint key space (collective apply, instance liveness, per-stream
/// event locks). The value feeds <c>pg_try_advisory_xact_lock(bigint)</c>, which accepts the full
/// signed 64-bit range — so, unlike the previous <c>Math.Abs(...) % int.MaxValue</c> form, there is
/// nothing to truncate and no <see cref="OverflowException"/> to hit on <see cref="int.MinValue"/>.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaInitializationLockKeyTests.cs</tests>
public static class SchemaInitializationLockKey {
  private const string KEY_NAMESPACE = "wh_schema_init:";

  /// <summary>
  /// The advisory-lock key guarding schema initialization for <paramref name="schema"/>.
  /// </summary>
  /// <param name="schema">
  /// The target schema name. An unset value (null or empty) normalizes to <c>public</c>, matching
  /// how the migration SQL transform resolves it — otherwise two instances of the same service,
  /// one configured with an explicit <c>public</c> and one with no schema at all, would take
  /// different locks while migrating the same physical schema.
  /// </param>
  /// <returns>A signed 64-bit key suitable for <c>pg_try_advisory_xact_lock(bigint)</c>.</returns>
  public static long Compute(string? schema) {
    var effectiveSchema = string.IsNullOrEmpty(schema) ? "public" : schema;
    return Fnv1a64.Compute(KEY_NAMESPACE + effectiveSchema);
  }
}
