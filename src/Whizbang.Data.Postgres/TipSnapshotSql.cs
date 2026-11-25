// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The snapshot writes and removals whose rule depends on a snapshot's kind (#2463): a floor, which the reap counts and
/// only the reap-boundary bootstrap moves for an ephemeral perspective, or the one tip a perspective applying both
/// ephemeral and durable events keeps near its cursor. Shared by both Postgres drivers, so a stream keeps the same
/// snapshots whichever driver a consumer configured.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>A floor written at the tip's event takes its place, as the newest floor.</description></item>
/// <item><description>A tip replaces the stream's previous tip and is kept only above the newest floor, at an event the
/// store can place: bodies at or below a floor may be reaped, so a replay from a tip there could cross them.</description></item>
/// <item><description>Pruning counts floors alone and removes a tip at or below the newest floor.</description></item>
/// <item><description>A late event removes a tip at or above it, which was taken without it.</description></item>
/// </list>
/// For a perspective that keeps no tip every rule reads as before: no row is a tip.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#the-tip-snapshot</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/TipSnapshotStoreTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperTipSnapshotStoreTests.cs</tests>
internal static class TipSnapshotSql {
  private const string PARAM_STREAM_ID = "p_stream_id";
  private const string PARAM_PERSPECTIVE_NAME = "p_perspective_name";

  // snapshot_version is read from the event the snapshot was taken at rather than passed in: the version is a property
  // of that event, so every caller would otherwise look up the same number and one that forgot would write a row nothing
  // can place in the stream. A snapshot_event_id with no row in the event store yields NULL, which selection and the reap
  // gate both read as "cannot be placed". A floor written over the tip at its event becomes the newest floor (#2463).
  private const string FLOOR_SQL = """
    INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_commit_sequence, snapshot_version, collectives_through_sequence)
    VALUES (@p_stream_id, @p_perspective_name, @p_snapshot_event_id, @p_snapshot_data::jsonb,
      COALESCE(
        (SELECT MAX(sequence_number) + 1 FROM wh_perspective_snapshots
         WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name), 1),
      @p_snapshot_commit_sequence,
      (SELECT es.version FROM wh_event_store es WHERE es.event_id = @p_snapshot_event_id),
      @p_collectives_through_sequence)
    ON CONFLICT (stream_id, perspective_name, snapshot_event_id) DO UPDATE
    SET snapshot_data = EXCLUDED.snapshot_data,
        snapshot_commit_sequence = COALESCE(EXCLUDED.snapshot_commit_sequence, wh_perspective_snapshots.snapshot_commit_sequence),
        snapshot_version = COALESCE(EXCLUDED.snapshot_version, wh_perspective_snapshots.snapshot_version),
        collectives_through_sequence = EXCLUDED.collectives_through_sequence,
        sequence_number = CASE WHEN wh_perspective_snapshots.is_tip THEN EXCLUDED.sequence_number ELSE wh_perspective_snapshots.sequence_number END,
        is_tip = FALSE,
        created_at = NOW()
    """;

  // The stream's previous tip goes; the new one is written only at an event of the stream above the newest floor, and
  // never over a floor at its event.
  private const string TIP_SQL = """
    WITH replaced AS (
      DELETE FROM wh_perspective_snapshots
      WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name AND is_tip
        AND snapshot_event_id <> @p_snapshot_event_id)
    INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_commit_sequence, snapshot_version, collectives_through_sequence, is_tip)
    SELECT @p_stream_id, @p_perspective_name, @p_snapshot_event_id, @p_snapshot_data::jsonb,
      COALESCE(
        (SELECT MAX(sequence_number) + 1 FROM wh_perspective_snapshots
         WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name), 1),
      @p_snapshot_commit_sequence, es.version, @p_collectives_through_sequence, TRUE
    FROM wh_event_store es
    WHERE es.event_id = @p_snapshot_event_id AND es.stream_id = @p_stream_id
      AND es.version > COALESCE(
        (SELECT MAX(f.snapshot_version) FROM wh_perspective_snapshots f
         WHERE f.stream_id = @p_stream_id AND f.perspective_name = @p_perspective_name AND NOT f.is_tip), 0)
    ON CONFLICT (stream_id, perspective_name, snapshot_event_id) DO UPDATE
    SET snapshot_data = EXCLUDED.snapshot_data,
        snapshot_commit_sequence = COALESCE(EXCLUDED.snapshot_commit_sequence, wh_perspective_snapshots.snapshot_commit_sequence),
        collectives_through_sequence = EXCLUDED.collectives_through_sequence,
        created_at = NOW()
    WHERE wh_perspective_snapshots.is_tip
    """;

  // Floors beyond the newest keep_count go, by the order they were written; a tip goes once a floor reaches it.
  private const string PRUNE_SQL = """
    DELETE FROM wh_perspective_snapshots s
    WHERE s.stream_id = @p_stream_id AND s.perspective_name = @p_perspective_name
      AND ((s.is_tip AND s.snapshot_version <= (
              SELECT MAX(f.snapshot_version) FROM wh_perspective_snapshots f
              WHERE f.stream_id = @p_stream_id AND f.perspective_name = @p_perspective_name AND NOT f.is_tip))
        OR (NOT s.is_tip AND s.sequence_number NOT IN (
              SELECT k.sequence_number FROM wh_perspective_snapshots k
              WHERE k.stream_id = @p_stream_id AND k.perspective_name = @p_perspective_name AND NOT k.is_tip
              ORDER BY k.sequence_number DESC
              LIMIT @p_keep_count)))
    """;

  // A tip at or above the late event, or one either cannot be placed, goes; a floor never does.
  private const string DELETE_TIP_FROM_SQL = """
    DELETE FROM wh_perspective_snapshots s
    WHERE s.stream_id = @p_stream_id AND s.perspective_name = @p_perspective_name AND s.is_tip
      AND COALESCE(s.snapshot_version >= (
        SELECT es.version FROM wh_event_store es WHERE es.event_id = @p_event_id AND es.stream_id = @p_stream_id), TRUE)
    """;

  /// <summary>Writes a floor, on an OPEN connection; a write at the tip's event makes it the newest floor.</summary>
  internal static Task CreateFloorAsync(NpgsqlConnection connection, SnapshotWrite write, CancellationToken ct) =>
    _writeAsync(connection, FLOOR_SQL, write, ct);

  /// <summary>Writes the stream's tip, on an OPEN connection, by the rules above.</summary>
  internal static Task CreateTipAsync(NpgsqlConnection connection, SnapshotWrite write, CancellationToken ct) =>
    _writeAsync(connection, TIP_SQL, write, ct);

  /// <summary>Prunes the stream's floors to <paramref name="keepCount"/> and removes a tip a floor reached; returns the
  /// rows removed.</summary>
  internal static async Task<int> PruneAsync(
      NpgsqlConnection connection, Guid streamId, string perspectiveName, int keepCount, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand(PRUNE_SQL, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, streamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, perspectiveName);
    cmd.Parameters.AddWithValue("p_keep_count", keepCount);
    return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <summary>Removes the stream's tip at or above <paramref name="eventId"/>, on an OPEN connection.</summary>
  internal static async Task DeleteTipFromAsync(
      NpgsqlConnection connection, Guid streamId, string perspectiveName, Guid eventId, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand(DELETE_TIP_FROM_SQL, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, streamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, perspectiveName);
    cmd.Parameters.AddWithValue("p_event_id", eventId);
    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  private static async Task _writeAsync(NpgsqlConnection connection, string sql, SnapshotWrite write, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, write.StreamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, write.PerspectiveName);
    cmd.Parameters.AddWithValue("p_snapshot_event_id", write.SnapshotEventId);
    cmd.Parameters.Add(new NpgsqlParameter("p_snapshot_data", NpgsqlDbType.Jsonb) { Value = write.SnapshotData.RootElement.GetRawText() });
    cmd.Parameters.Add(new NpgsqlParameter("p_snapshot_commit_sequence", NpgsqlDbType.Bigint) {
      Value = (object?)write.SnapshotCommitSequence ?? DBNull.Value
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_collectives_through_sequence", NpgsqlDbType.Bigint) {
      Value = (object?)write.CollectivesThroughSequence ?? DBNull.Value
    });
    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }
}

/// <summary>One snapshot write (#2463): where it is taken, the model, and its two marks.</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="PerspectiveName">The perspective.</param>
/// <param name="SnapshotEventId">The last event the snapshot holds.</param>
/// <param name="SnapshotCommitSequence">That event's commit sequence, or null.</param>
/// <param name="SnapshotData">The serialized model.</param>
/// <param name="CollectivesThroughSequence">The commit sequence through which the row's collectives were complete, or null.</param>
internal readonly record struct SnapshotWrite(
    Guid StreamId,
    string PerspectiveName,
    Guid SnapshotEventId,
    long? SnapshotCommitSequence,
    JsonDocument SnapshotData,
    long? CollectivesThroughSequence);
