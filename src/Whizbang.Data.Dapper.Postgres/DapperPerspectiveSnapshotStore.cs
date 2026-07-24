using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Perspectives;

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

    const string sql = """
      INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number)
      VALUES (@p_stream_id, @p_perspective_name, @p_snapshot_event_id, @p_snapshot_data::jsonb,
        COALESCE(
          (SELECT MAX(sequence_number) + 1 FROM wh_perspective_snapshots
           WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name), 1))
      ON CONFLICT (stream_id, perspective_name, snapshot_event_id) DO UPDATE
      SET snapshot_data = EXCLUDED.snapshot_data,
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
    return result is true;
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
