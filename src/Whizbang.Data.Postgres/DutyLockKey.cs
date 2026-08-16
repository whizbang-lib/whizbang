namespace Whizbang.Data.Postgres;

/// <summary>
/// Computes the session advisory-lock key for a duty — the primitive behind held-by-election
/// capabilities. Same process-stable derivation discipline as <see cref="SchemaInitializationLockKey"/>:
/// the key must be identical in every process contending for the same duty in the same schema,
/// and namespaced so it cannot collide with the other advisory-lock families sharing Postgres's
/// single-bigint key space (schema init, collective apply, instance liveness, per-stream locks).
/// </summary>
/// <docs>operations/startup/capabilities-and-duties</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DutyLockKeyTests.cs</tests>
public static class DutyLockKey {
  private const string KEY_NAMESPACE = "wh_duty:";

  /// <summary>
  /// The advisory-lock key for <paramref name="duty"/> scoped to <paramref name="schema"/>.
  /// </summary>
  /// <param name="schema">The service's schema. An unset value (null or empty) normalizes to
  /// <c>public</c>, and quoting is stripped, so every spelling of the same physical schema takes
  /// the same lock — the same rule <see cref="SchemaInitializationLockKey"/> follows.</param>
  /// <param name="duty">The duty name (e.g. <c>migrator</c>, <c>maintainer</c>).</param>
  /// <returns>A signed 64-bit key suitable for <c>pg_try_advisory_lock(bigint)</c>.</returns>
  public static long Compute(string? schema, string duty) {
    ArgumentException.ThrowIfNullOrEmpty(duty);
    var effectiveSchema = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", "", StringComparison.Ordinal);
    return Fnv1a64.Compute(KEY_NAMESPACE + effectiveSchema + ":" + duty);
  }
}
