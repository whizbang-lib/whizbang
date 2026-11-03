// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The ephemeral reap's destruction seam, as both Postgres work coordinators run it: which bodies this cycle will reap,
/// the hold a hook's Cancel or Defer records, the failure count a throwing hook records, and the maintenance call that
/// reaps only the bodies the hook was offered.
/// </summary>
/// <remarks>
/// <para>
/// One copy, so the drivers cannot drift. The Dapper coordinator used to inherit the interface's defaults for all four:
/// nothing about to be reaped, holds that recorded nothing, a failure count that forced the delete at once, and a reap
/// that deleted every eligible body. A Dapper host with a destruction hook registered was offered nothing while the
/// bodies went (#2039).
/// </para>
/// <para>
/// Each coordinator supplies a command on its own connection and the prefix it qualifies names with: the quoted schema
/// followed by a dot (<c>"svc".</c>), or empty where the caller resolves through <c>search_path</c>. It is always built
/// by <see cref="PgIdentifier"/>, never from input, and every value is a bound parameter.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#destruction-hooks-on-every-data-layer</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralDestructionTargetSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralDestructionHoldSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapSeenSetTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperEphemeralDestructionHookTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier and the table names are constants. Every value is a bound parameter.")]
internal static class EphemeralDestructionSql {
  private const string HOLD_TABLE = "wh_event_destruction_hold";
  private const string IDS = "ids";
  private const string UNTIL = "until";

  /// <summary>
  /// The bodies this maintenance cycle will reap: the exact predicate of the reap's DELETE (<c>wh_perform_maintenance</c>,
  /// migration 310), as a SELECT. Ephemeral (flags&amp;8), not held, aged past its grace window, past its AfterTtl
  /// expiry, consumed (no unprocessed work item), and snapshot-covered.
  /// </summary>
  internal static async Task<IReadOnlyList<EphemeralDestructionTarget>> CollectAboutToReapAsync(
      DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText =
      "SELECT es.event_id, es.stream_id, es.event_type " +
      $"FROM {prefix}wh_event_body eb " +
      $"JOIN {prefix}wh_event_store es ON es.event_id = eb.event_id " +
      $"LEFT JOIN {prefix}wh_ephemeral_type_grace g ON g.event_type = es.event_type " +
      "WHERE (es.flags & 8) = 8 " +
      // E2-3: skip bodies a hook already held (Cancel/Defer); they are not re-offered until the hold lapses.
      $"AND NOT EXISTS (SELECT 1 FROM {prefix}{HOLD_TABLE} h WHERE h.event_id = eb.event_id AND h.hold_until > NOW()) " +
      "AND es.created_at < NOW() - (COALESCE(g.grace_seconds, " +
      $"    (SELECT setting_value::int FROM {prefix}wh_settings WHERE setting_key = 'ephemeral_rewind_grace_seconds'), 300) " +
      "  * INTERVAL '1 second') " +
      // #2018: the reap's TTL floor. An AfterTtl body is kept until past its own expiry, so it is not offered before then:
      // a body offered and kept was reported destroyed while it survived. No key (Sourced / WhenConsumed) passes.
      "AND (eb.metadata ->> 'ephemeral_expires_at' IS NULL " +
      "  OR (eb.metadata ->> 'ephemeral_expires_at')::timestamptz < NOW()) " +
      $"AND NOT EXISTS (SELECT 1 FROM {prefix}wh_perspective_events pe WHERE pe.event_id = eb.event_id AND pe.processed_at IS NULL) " +
      $"AND NOT EXISTS (SELECT 1 FROM {prefix}wh_message_associations ma WHERE ma.normalized_message_type = es.event_type " +
      "  AND ma.association_type = 'perspective' " +
      $"  AND NOT EXISTS (SELECT 1 FROM {prefix}wh_perspective_snapshots s WHERE s.stream_id = es.stream_id " +
      // 209: by version, matching the migration. Any drift from the DELETE this mirrors reports bodies that survive or
      // hides bodies that go.
      "    AND s.perspective_name = ma.target_name AND s.snapshot_version >= es.version))";
    var targets = new List<EphemeralDestructionTarget>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      targets.Add(new EphemeralDestructionTarget(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
    }
    return targets;
  }

  /// <summary>Holds every body in <paramref name="eventIds"/> to <paramref name="holdUntil"/>; a later decision overwrites it.</summary>
  internal static async Task HoldAsync(
      DbCommand cmd, string prefix, IReadOnlyList<Guid> eventIds, DateTimeOffset holdUntil, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"INSERT INTO {prefix}{HOLD_TABLE} (event_id, hold_until) " +
      $"SELECT unnest(@{IDS}), @{UNTIL} ON CONFLICT (event_id) DO UPDATE SET hold_until = EXCLUDED.hold_until";
    cmd.Parameters.Add(_ids(eventIds));
    cmd.Parameters.Add(new NpgsqlParameter(UNTIL, NpgsqlDbType.TimestampTz) { Value = holdUntil.UtcDateTime });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Records one more failure for each body and returns the batch's highest count. Under the cap each body is held to
  /// its retry instant: the midpoint to its TTL expiry when it carries one, else <paramref name="retryHoldUntil"/>. Past
  /// the cap the policy decides: forced (<c>-infinity</c>, which the reap's hold gate lets through) or kept
  /// (<c>infinity</c>); <see cref="OnDestroyFailure.ForceDeleteImmediately"/> forces at once.
  /// </summary>
  internal static async Task<int> RecordFailureAsync(
      DbCommand cmd, string prefix, IReadOnlyList<Guid> eventIds, DateTimeOffset retryHoldUntil, int maxRetries,
      OnDestroyFailure onFailure, CancellationToken cancellationToken) {
    var hold = prefix + HOLD_TABLE;
    // @pol: 2=ForceDeleteImmediately ('-infinity' now), 1=RetryThenKeep (past cap => keep, 'infinity'),
    // 0=RetryThenForcedDelete (past cap => force, '-infinity').
    cmd.CommandText =
      "WITH src AS ( " +
      "  SELECT id AS event_id, " +
      "    CASE WHEN @pol = 2 THEN '-infinity'::timestamptz " +
      "         WHEN eb.metadata ->> 'ephemeral_expires_at' IS NOT NULL " +
      "         THEN NOW() + ((eb.metadata ->> 'ephemeral_expires_at')::timestamptz - NOW()) / 2 " +
      $"         ELSE @{UNTIL} END AS retry_until " +
      $"  FROM unnest(@{IDS}) AS id " +
      $"  LEFT JOIN {prefix}wh_event_body eb ON eb.event_id = id), " +
      "upserted AS ( " +
      $"  INSERT INTO {hold} (event_id, hold_until, failure_count) " +
      "  SELECT event_id, retry_until, 1 FROM src " +
      "  ON CONFLICT (event_id) DO UPDATE SET " +
      $"    failure_count = {hold}.failure_count + 1, " +
      "    hold_until = CASE " +
      "      WHEN @pol = 2 THEN '-infinity'::timestamptz " +
      $"      WHEN {hold}.failure_count + 1 > @max " +
      "        THEN (CASE WHEN @pol = 1 THEN 'infinity'::timestamptz ELSE '-infinity'::timestamptz END) " +
      "      ELSE EXCLUDED.hold_until END " +
      "  RETURNING failure_count) " +
      "SELECT COALESCE(MAX(failure_count), 0) FROM upserted";
    cmd.Parameters.Add(_ids(eventIds));
    cmd.Parameters.Add(new NpgsqlParameter(UNTIL, NpgsqlDbType.TimestampTz) { Value = retryHoldUntil.UtcDateTime });
    cmd.Parameters.Add(new NpgsqlParameter("max", NpgsqlDbType.Integer) { Value = maxRetries });
    cmd.Parameters.Add(new NpgsqlParameter("pol", NpgsqlDbType.Integer) { Value = (int)onFailure });
    return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  /// <summary>
  /// Runs the maintenance tasks (<c>wh_perform_maintenance</c>, migration 310). With <paramref name="reapOnly"/> the
  /// ephemeral reap deletes only those bodies, the ones a hook was offered; <see langword="null"/> reaps every eligible body.
  /// </summary>
  internal static async Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(
      DbCommand cmd, string prefix, IReadOnlyCollection<Guid>? reapOnly, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT * FROM {prefix}wh_perform_maintenance(@reap_ids)";
    cmd.Parameters.Add(new NpgsqlParameter("reap_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) {
      Value = reapOnly is null ? DBNull.Value : reapOnly.ToArray(),
    });
    var results = new List<MaintenanceResult>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      results.Add(new MaintenanceResult(reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetString(3)));
    }
    return results;
  }

  private static NpgsqlParameter _ids(IReadOnlyList<Guid> eventIds) =>
    new(IDS, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = eventIds.ToArray() };
}
