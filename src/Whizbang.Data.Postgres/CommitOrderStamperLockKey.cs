// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Computes the session advisory-lock key the commit-order stamper elects its leader on.
/// </summary>
/// <remarks>
/// Stamping is per schema: the leader stamps its own service's <c>wh_event_store</c>. A Postgres
/// advisory lock is per database. A key shared by every schema therefore let the first service
/// in a database to elect a stamper exclude every other service's stamper, and those services'
/// events were never stamped. The key is scoped to the schema, with the same derivation
/// discipline as <see cref="DutyLockKey"/>: identical in every instance of one service, distinct
/// across schemas, and namespaced so it cannot collide with the other advisory-lock families
/// sharing Postgres's single-bigint key space.
/// </remarks>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitOrderStamperLockKeyTests.cs</tests>
public static class CommitOrderStamperLockKey {
  private const string KEY_NAMESPACE = "wh_commit_stamper:";

  /// <summary>
  /// The stamper's advisory-lock key for <paramref name="schema"/>.
  /// </summary>
  /// <param name="schema">The service's schema. An unset value (null or empty) normalizes to
  /// <c>public</c>, and quoting is stripped, so every spelling of the same physical schema takes
  /// the same lock — the same rule <see cref="DutyLockKey"/> follows.</param>
  /// <param name="baseKey">The configured
  /// <see cref="Whizbang.Core.Notifications.CommitOrderStamperOptions.AdvisoryLockKey"/>, so an
  /// operator who changes it to avoid a collision still gets a different lock.</param>
  /// <returns>A signed 64-bit key suitable for <c>pg_try_advisory_lock(bigint)</c>.</returns>
  public static long Compute(string? schema, long baseKey) {
    var effectiveSchema = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", "", StringComparison.Ordinal);
    return FnvHash64.Compute(KEY_NAMESPACE + baseKey.ToString(CultureInfo.InvariantCulture) + ":" + effectiveSchema);
  }
}
