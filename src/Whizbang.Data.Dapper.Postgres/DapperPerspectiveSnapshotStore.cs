// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Dapper/Npgsql implementation of <see cref="IPerspectiveSnapshotStore"/>.
/// Stores and retrieves perspective snapshots for efficient rewind after late-arriving events.
/// </summary>
/// <docs>fundamentals/perspectives/snapshots</docs>
public sealed partial class DapperPerspectiveSnapshotStore(
  string connectionString,
  ILogger<DapperPerspectiveSnapshotStore>? logger = null) : IPerspectiveSnapshotStore {

  private const string PARAM_STREAM_ID = "p_stream_id";
  private const string PARAM_PERSPECTIVE_NAME = "p_perspective_name";

  public async Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    // snapshot_version is read from the event the snapshot was taken at rather than passed in: the
    // version is a property of that event, so every caller would otherwise look up the same number and
    // one that forgot would write a row nothing can place in the stream. A snapshot_event_id with no row
    // in the event store yields NULL, which selection and the reap gate both read as "cannot be placed".
    const string sql = """
      INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version)
      VALUES (@p_stream_id, @p_perspective_name, @p_snapshot_event_id, @p_snapshot_data::jsonb,
        COALESCE(
          (SELECT MAX(sequence_number) + 1 FROM wh_perspective_snapshots
           WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name), 1),
        (SELECT es.version FROM wh_event_store es WHERE es.event_id = @p_snapshot_event_id))
      ON CONFLICT (stream_id, perspective_name, snapshot_event_id) DO UPDATE
      SET snapshot_data = EXCLUDED.snapshot_data,
          snapshot_version = COALESCE(EXCLUDED.snapshot_version, wh_perspective_snapshots.snapshot_version),
          created_at = NOW()
      """;

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, streamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, perspectiveName);
    cmd.Parameters.AddWithValue("p_snapshot_event_id", snapshotEventId);
    cmd.Parameters.Add(new NpgsqlParameter("p_snapshot_data", NpgsqlDbType.Jsonb) { Value = snapshotData.RootElement.GetRawText() });

    await cmd.ExecuteNonQueryAsync(ct);

    if (logger?.IsEnabled(LogLevel.Debug) == true) {
      LogSnapshotCreated(logger, perspectiveName, streamId, snapshotEventId);
    }
  }

  public async Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    const string sql = """
      SELECT snapshot_event_id, snapshot_data
      FROM wh_perspective_snapshots
      WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name
      ORDER BY sequence_number DESC
      LIMIT 1
      """;

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, streamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, perspectiveName);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct)) {
      return null;
    }

    var snapshotEventId = reader.GetGuid(0);
    var snapshotJson = reader.GetString(1);
    return (snapshotEventId, JsonDocument.Parse(snapshotJson));
  }

  /// <summary>
  /// Returns the latest snapshot that lies before <paramref name="beforeEventId"/> in the stream's own
  /// order. Version is the stream's own counter and cannot invert; commit sequence and event id both can,
  /// so neither answers "is this snapshot before that event" within a single stream (#1355).
  /// </summary>
  /// <remarks>
  /// Two NULL cases fall out of the SQL and both mean "no usable snapshot": a snapshot written before the
  /// column existed, excluded explicitly, and a triggering event with no row in the event store, whose
  /// subquery yields NULL so the comparison matches nothing. Both cost a fuller replay, which reproduces
  /// the row. Returning a snapshot on a guess skips the events between it and the trigger, and a read
  /// model short of events has nothing pending to repair it.
  /// </remarks>
  /// <docs>fundamentals/perspectives/snapshots</docs>
  public async Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
      Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    // Resolve where the triggering event sits in its stream first, because the answer decides which
    // order can be trusted. Two statements rather than one branching query: a rewind is rare and the
    // reader of this code is not.
    const string versionSql = """
      SELECT es.version FROM wh_event_store es WHERE es.event_id = @p_before_event_id
      """;
    int? triggerVersion;
    await using (var versionCmd = new NpgsqlCommand(versionSql, connection)) {
      versionCmd.Parameters.AddWithValue("p_before_event_id", beforeEventId);
      var raw = await versionCmd.ExecuteScalarAsync(ct);
      triggerVersion = raw is null or DBNull ? null : Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    // The event is placed, so version order is available and is the only order worth trusting. A
    // snapshot recording no version predates the column; nothing can order it against this event, so it
    // is refused rather than guessed at.
    const string byVersionSql = """
      SELECT s.snapshot_event_id, s.snapshot_version, s.snapshot_data
      FROM wh_perspective_snapshots s
      WHERE s.stream_id = @p_stream_id
        AND s.perspective_name = @p_perspective_name
        AND s.snapshot_version IS NOT NULL
        AND s.snapshot_version < @p_before_version
      ORDER BY s.snapshot_version DESC
      LIMIT 1
      """;

    // The event is not placed, which means this store keeps no version for it. A store with no versions
    // has no retroactive stamping to invert against them either, so event id order is the only order it
    // has and is as sound here as it ever was. Refusing would replay every rewind from event zero for a
    // store that never had the defect.
    const string byEventIdSql = """
      SELECT s.snapshot_event_id, s.snapshot_version, s.snapshot_data
      FROM wh_perspective_snapshots s
      WHERE s.stream_id = @p_stream_id
        AND s.perspective_name = @p_perspective_name
        AND s.snapshot_event_id < @p_before_event_id
      ORDER BY s.snapshot_event_id DESC
      LIMIT 1
      """;

    await using var cmd = new NpgsqlCommand(triggerVersion.HasValue ? byVersionSql : byEventIdSql, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, streamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, perspectiveName);
    if (triggerVersion.HasValue) {
      cmd.Parameters.AddWithValue("p_before_version", triggerVersion.Value);
    } else {
      cmd.Parameters.AddWithValue("p_before_event_id", beforeEventId);
    }

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct)) {
      return null;
    }
    var snapshotEventId = reader.GetGuid(0);
    int? snapshotVersion = await reader.IsDBNullAsync(1, ct) ? null : reader.GetInt32(1);
    var snapshotJson = reader.GetString(2);
    return (snapshotEventId, snapshotVersion, JsonDocument.Parse(snapshotJson));
  }

  public async Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    const string sql = """
      SELECT snapshot_event_id, snapshot_data
      FROM wh_perspective_snapshots
      WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name
        AND snapshot_event_id < @p_before_event_id
      ORDER BY snapshot_event_id DESC
      LIMIT 1
      """;

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, streamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, perspectiveName);
    cmd.Parameters.AddWithValue("p_before_event_id", beforeEventId);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct)) {
      return null;
    }

    var snapshotEventId = reader.GetGuid(0);
    var snapshotJson = reader.GetString(1);
    return (snapshotEventId, JsonDocument.Parse(snapshotJson));
  }

  public async Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    const string sql = """
      SELECT EXISTS(
        SELECT 1 FROM wh_perspective_snapshots
        WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name)
      """;

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, streamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, perspectiveName);

    var result = await cmd.ExecuteScalarAsync(ct);
    return ScalarResult.IsTrue(result);
  }

  public async Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    const string sql = """
      DELETE FROM wh_perspective_snapshots
      WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name
        AND sequence_number NOT IN (
          SELECT sequence_number FROM wh_perspective_snapshots
          WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name
          ORDER BY sequence_number DESC
          LIMIT @p_keep_count)
      """;

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, streamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, perspectiveName);
    cmd.Parameters.AddWithValue("p_keep_count", keepCount);

    var deleted = await cmd.ExecuteNonQueryAsync(ct);
    if (deleted > 0 && logger?.IsEnabled(LogLevel.Debug) == true) {
      LogSnapshotsPruned(logger, deleted, perspectiveName, streamId, keepCount);
    }
  }

  public async Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    const string sql = """
      DELETE FROM wh_perspective_snapshots
      WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name
      """;

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue(PARAM_STREAM_ID, streamId);
    cmd.Parameters.AddWithValue(PARAM_PERSPECTIVE_NAME, perspectiveName);

    await cmd.ExecuteNonQueryAsync(ct);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Snapshot created for {PerspectiveName} stream {StreamId} at event {SnapshotEventId}")]
  static partial void LogSnapshotCreated(ILogger logger, string perspectiveName, Guid streamId, Guid snapshotEventId);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Pruned {Deleted} old snapshots for {PerspectiveName} stream {StreamId} (keeping {KeepCount})")]
  static partial void LogSnapshotsPruned(ILogger logger, int deleted, string perspectiveName, Guid streamId, int keepCount);
}
