// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The statements behind a paced backfill's durable progress (#2092): queue, read, record a window request, apply a
/// window answer, end. Each coordinator supplies a command on its own connection and the prefix it qualifies names with,
/// built by <see cref="PgIdentifier"/>; every value is a bound parameter.
/// </summary>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SubscriptionBackfillStoreTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier and the table name is a constant. Every value is a bound parameter.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations", Justification = "NpgsqlDbType composes Array with the element type by design.")]
internal static class SubscriptionBackfillSql {
  /// <summary>The backfill table.</summary>
  internal const string TABLE = "wh_subscription_backfills";

  private const string COLUMNS =
    "backfill_id, owner_service_id, owner_service_name, event_type, cursor_commit_sequence, window_number, answer_due_by, " +
    "window_attempts, windows_requested, events_received, first_commit_sequence, head_commit_sequence, last_requested_at, " +
    "started_at, completed_at, outcome";

  private const string P_ID = "p_id";
  private const string P_NOW = "p_now";
  private const string P_WINDOW = "p_window";

  /// <summary>Queues one backfill per seed; a pair already present keeps its row.</summary>
  internal static async Task EnqueueAsync(
      DbCommand cmd, string prefix, IReadOnlyList<SubscriptionBackfillSeed> seeds, CancellationToken cancellationToken) {
    var ids = new Guid[seeds.Count];
    var owners = new Guid[seeds.Count];
    var names = new string[seeds.Count];
    var types = new string[seeds.Count];
    for (var i = 0; i < seeds.Count; i++) {
      ids[i] = TrackedGuid.New().Value;
      owners[i] = seeds[i].OwnerServiceId;
      names[i] = seeds[i].OwnerServiceName;
      types[i] = seeds[i].EventType;
    }
    cmd.CommandText =
      $"INSERT INTO {prefix}{TABLE} (backfill_id, owner_service_id, owner_service_name, event_type) " +
      "SELECT * FROM unnest(@p_ids::uuid[], @p_owners::uuid[], @p_names::text[], @p_types::text[]) " +
      "ON CONFLICT (owner_service_id, event_type) DO NOTHING";
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
    cmd.Parameters.Add(new NpgsqlParameter("p_owners", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = owners });
    cmd.Parameters.Add(new NpgsqlParameter("p_names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });
    cmd.Parameters.Add(new NpgsqlParameter("p_types", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = types });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Every backfill, running and ended.</summary>
  internal static async Task<IReadOnlyList<SubscriptionBackfillCursor>> ReadAllAsync(
      DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {COLUMNS} FROM {prefix}{TABLE} ORDER BY started_at, backfill_id";
    return await _readAsync(cmd, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Records a window request: the same window again counts an attempt, a new window starts at one.</summary>
  internal static async Task MarkRequestedAsync(
      DbCommand cmd, string prefix, Guid backfillId, long window, DateTimeOffset now, DateTimeOffset answerDueBy,
      CancellationToken cancellationToken) {
    cmd.CommandText =
      $"UPDATE {prefix}{TABLE} SET " +
      "window_attempts = CASE WHEN window_number = @p_window THEN window_attempts + 1 ELSE 1 END, " +
      "window_number = @p_window, answer_due_by = @p_due, windows_requested = windows_requested + 1, last_requested_at = @p_now " +
      "WHERE backfill_id = @p_id AND completed_at IS NULL";
    cmd.Parameters.Add(new NpgsqlParameter(P_ID, NpgsqlDbType.Uuid) { Value = backfillId });
    cmd.Parameters.Add(new NpgsqlParameter(P_WINDOW, NpgsqlDbType.Bigint) { Value = window });
    cmd.Parameters.Add(new NpgsqlParameter("p_due", NpgsqlDbType.TimestampTz) { Value = answerDueBy.UtcDateTime });
    cmd.Parameters.Add(new NpgsqlParameter(P_NOW, NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Takes back a window request whose send failed: the row returns to the state it was requested from. Applies only
  /// while that request is still the latest one recorded (one more than the requests counted before it) and still awaits
  /// its answer, so an answer that landed meanwhile, a later request, or an end stands.
  /// </summary>
  internal static async Task WithdrawRequestAsync(
      DbCommand cmd, string prefix, SubscriptionBackfillCursor requestedFrom, long window, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"UPDATE {prefix}{TABLE} SET " +
      "window_number = @p_from_window, answer_due_by = @p_from_due, window_attempts = @p_from_attempts, " +
      "windows_requested = @p_from_requested, last_requested_at = @p_from_last " +
      "WHERE backfill_id = @p_id AND window_number = @p_window AND windows_requested = @p_from_requested + 1 " +
      "AND answer_due_by IS NOT NULL AND completed_at IS NULL";
    cmd.Parameters.Add(new NpgsqlParameter(P_ID, NpgsqlDbType.Uuid) { Value = requestedFrom.BackfillId });
    cmd.Parameters.Add(new NpgsqlParameter(P_WINDOW, NpgsqlDbType.Bigint) { Value = window });
    cmd.Parameters.Add(new NpgsqlParameter("p_from_window", NpgsqlDbType.Bigint) { Value = requestedFrom.Window });
    cmd.Parameters.Add(new NpgsqlParameter("p_from_due", NpgsqlDbType.TimestampTz) {
      Value = requestedFrom.AnswerDueBy is { } due ? due.UtcDateTime : (object)DBNull.Value,
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_from_attempts", NpgsqlDbType.Integer) { Value = requestedFrom.WindowAttempts });
    cmd.Parameters.Add(new NpgsqlParameter("p_from_requested", NpgsqlDbType.Bigint) { Value = requestedFrom.WindowsRequested });
    cmd.Parameters.Add(new NpgsqlParameter("p_from_last", NpgsqlDbType.TimestampTz) {
      Value = requestedFrom.LastRequestedAt is { } last ? last.UtcDateTime : (object)DBNull.Value,
    });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Applies a window's answer to the running backfill awaiting exactly that window, and returns the row after it; null
  /// when it applied to nothing (no such backfill, another window, no answer awaited, or already ended).
  /// </summary>
  internal static async Task<SubscriptionBackfillCursor?> RecordAnswerAsync(
      DbCommand cmd, string prefix, BackfillWindowAnswer answer, DateTimeOffset now, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"UPDATE {prefix}{TABLE} SET " +
      "cursor_commit_sequence = COALESCE(@p_stopped, cursor_commit_sequence), " +
      "events_received = events_received + @p_count, " +
      "first_commit_sequence = COALESCE(first_commit_sequence, @p_first), " +
      "head_commit_sequence = COALESCE(@p_head, head_commit_sequence), " +
      "answer_due_by = NULL, " +
      "completed_at = CASE WHEN @p_has_more THEN NULL ELSE @p_now END, " +
      "outcome = CASE WHEN @p_has_more THEN 0 ELSE 1 END " +
      "WHERE backfill_id = @p_id AND window_number = @p_window AND answer_due_by IS NOT NULL AND completed_at IS NULL " +
      $"RETURNING {COLUMNS}";
    cmd.Parameters.Add(new NpgsqlParameter(P_ID, NpgsqlDbType.Uuid) { Value = answer.BackfillId });
    cmd.Parameters.Add(new NpgsqlParameter(P_WINDOW, NpgsqlDbType.Bigint) { Value = answer.Window });
    cmd.Parameters.Add(new NpgsqlParameter("p_stopped", NpgsqlDbType.Bigint) { Value = (object?)answer.StoppedAtCommitSequence ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_count", NpgsqlDbType.Bigint) { Value = (long)answer.EventCount });
    cmd.Parameters.Add(new NpgsqlParameter("p_first", NpgsqlDbType.Bigint) { Value = (object?)answer.FirstCommitSequence ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_head", NpgsqlDbType.Bigint) { Value = (object?)answer.HeadCommitSequence ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_has_more", NpgsqlDbType.Boolean) { Value = answer.HasMore });
    cmd.Parameters.Add(new NpgsqlParameter(P_NOW, NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
    var rows = await _readAsync(cmd, cancellationToken).ConfigureAwait(false);
    return rows.Count == 0 ? null : rows[0];
  }

  /// <summary>Ends a running backfill with an outcome; an ended one is left as it ended.</summary>
  internal static async Task EndAsync(
      DbCommand cmd, string prefix, Guid backfillId, SubscriptionBackfillOutcome outcome, DateTimeOffset now,
      CancellationToken cancellationToken) {
    cmd.CommandText =
      $"UPDATE {prefix}{TABLE} SET completed_at = @p_now, outcome = @p_outcome, answer_due_by = NULL " +
      "WHERE backfill_id = @p_id AND completed_at IS NULL";
    cmd.Parameters.Add(new NpgsqlParameter(P_ID, NpgsqlDbType.Uuid) { Value = backfillId });
    cmd.Parameters.Add(new NpgsqlParameter("p_outcome", NpgsqlDbType.Smallint) { Value = (short)outcome });
    cmd.Parameters.Add(new NpgsqlParameter(P_NOW, NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private static async Task<IReadOnlyList<SubscriptionBackfillCursor>> _readAsync(DbCommand cmd, CancellationToken cancellationToken) {
    var rows = new List<SubscriptionBackfillCursor>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      rows.Add(new SubscriptionBackfillCursor {
        BackfillId = reader.GetGuid(0),
        OwnerServiceId = reader.GetGuid(1),
        OwnerServiceName = reader.GetString(2),
        EventType = reader.GetString(3),
        CursorCommitSequence = await _nullableInt64Async(reader, 4, cancellationToken).ConfigureAwait(false),
        Window = reader.GetInt64(5),
        AnswerDueBy = await _nullableInstantAsync(reader, 6, cancellationToken).ConfigureAwait(false),
        WindowAttempts = reader.GetInt32(7),
        WindowsRequested = reader.GetInt64(8),
        EventsReceived = reader.GetInt64(9),
        FirstCommitSequence = await _nullableInt64Async(reader, 10, cancellationToken).ConfigureAwait(false),
        HeadCommitSequence = await _nullableInt64Async(reader, 11, cancellationToken).ConfigureAwait(false),
        LastRequestedAt = await _nullableInstantAsync(reader, 12, cancellationToken).ConfigureAwait(false),
        StartedAt = _instant(reader, 13),
        CompletedAt = await _nullableInstantAsync(reader, 14, cancellationToken).ConfigureAwait(false),
        Outcome = (SubscriptionBackfillOutcome)reader.GetInt16(15),
      });
    }
    return rows;
  }

  private static async Task<long?> _nullableInt64Async(DbDataReader reader, int ordinal, CancellationToken cancellationToken) {
    if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)) {
      return null;
    }
    return reader.GetInt64(ordinal);
  }

  private static async Task<DateTimeOffset?> _nullableInstantAsync(DbDataReader reader, int ordinal, CancellationToken cancellationToken) {
    if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)) {
      return null;
    }
    return _instant(reader, ordinal);
  }

  private static DateTimeOffset _instant(DbDataReader reader, int ordinal) =>
    new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
