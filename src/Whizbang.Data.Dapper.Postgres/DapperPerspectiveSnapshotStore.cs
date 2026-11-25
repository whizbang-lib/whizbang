// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
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

  public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
    CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, snapshotCommitSequence: null, snapshotData, collectivesThroughSequence: null, ct);

  /// <summary>
  /// Writes a snapshot with its commit sequence and the commit sequence through which its row's collectives were complete
  /// (#1963). The collectives mark is written with the data it describes, so a write of the same snapshot replaces it,
  /// with none when the write records none; a commit sequence the write does not know keeps the one already recorded.
  /// </summary>
  /// <docs>fundamentals/perspectives/snapshots#collectives</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperPerspectiveSnapshotStoreTests.cs:SnapshotStore_RecordsTheCollectivesCompleteThroughMark_AndTheFloorAndTheNewestSnapshotCarryItAsync</tests>
  public async Task CreateSnapshotAsync(
      Guid streamId,
      string perspectiveName,
      Guid snapshotEventId,
      long? snapshotCommitSequence,
      JsonDocument snapshotData,
      long? collectivesThroughSequence,
      CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    // #2463: the write is shared with the other driver; a floor written at the tip's event takes its place.
    await TipSnapshotSql.CreateFloorAsync(
      connection,
      new SnapshotWrite(streamId, perspectiveName, snapshotEventId, snapshotCommitSequence, snapshotData, collectivesThroughSequence),
      ct);

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
  /// The rewind's snapshot floor: the snapshot <see cref="GetLatestSnapshotBeforeEventInStreamOrderAsync"/> selects, with
  /// the archive mark that tells a rewind from it to read through the archive (#1738).
  /// </summary>
  /// <docs>fundamentals/events/archive-store#rewind-past-an-archived-snapshot</docs>
  public async Task<PerspectiveSnapshotFloor?> GetSnapshotFloorAsync(
      Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);
    return await SnapshotStreamOrderLookup.FindFloorAsync(connection, streamId, perspectiveName, beforeEventId, ct);
  }

  /// <summary>The stream's newest snapshot with its marks (#1963); the lookup is shared with the EF Core driver.</summary>
  /// <docs>fundamentals/perspectives/snapshots#collectives</docs>
  public async Task<PerspectiveSnapshotFloor?> GetLatestSnapshotFloorAsync(
      Guid streamId, string perspectiveName, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);
    return await SnapshotStreamOrderLookup.FindLatestFloorAsync(connection, streamId, perspectiveName, ct);
  }

  /// <summary>
  /// Returns the latest snapshot that lies before <paramref name="beforeEventId"/> in the stream's own
  /// order. The choice of order, and why a snapshot recording no version is refused where the event can be
  /// placed and event id order is used where it cannot, lives in
  /// <see cref="SnapshotStreamOrderLookup"/> so both drivers answer identically (#1355).
  /// </summary>
  /// <docs>fundamentals/perspectives/snapshots</docs>
  public async Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
      Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);
    return await SnapshotStreamOrderLookup.FindAsync(connection, streamId, perspectiveName, beforeEventId, ct);
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

  /// <summary>
  /// Keeps the newest <paramref name="keepCount"/> floors and removes a tip a floor has reached (#2463); a tip is not one
  /// of the floors the retention counts.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events#the-tip-snapshot</docs>
  public async Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    var deleted = await TipSnapshotSql.PruneAsync(connection, streamId, perspectiveName, keepCount, ct);
    if (deleted > 0 && logger?.IsEnabled(LogLevel.Debug) == true) {
      LogSnapshotsPruned(logger, deleted, perspectiveName, streamId, keepCount);
    }
  }

  /// <summary>
  /// Writes the stream's tip (#2463): it replaces the previous tip and is kept only above the newest floor, at an event
  /// the store can place. The write is shared with the other driver.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events#the-tip-snapshot</docs>
  public async Task CreateTipSnapshotAsync(
      Guid streamId,
      string perspectiveName,
      Guid snapshotEventId,
      long? snapshotCommitSequence,
      JsonDocument snapshotData,
      long? collectivesThroughSequence,
      CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);
    await TipSnapshotSql.CreateTipAsync(
      connection,
      new SnapshotWrite(streamId, perspectiveName, snapshotEventId, snapshotCommitSequence, snapshotData, collectivesThroughSequence),
      ct);
  }

  /// <summary>The floor before the event, never a tip (#2463); the lookup is shared with the other driver.</summary>
  /// <docs>fundamentals/events/ephemeral-events#the-tip-snapshot</docs>
  public async Task<PerspectiveSnapshotFloor?> GetFloorSnapshotAsync(
      Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);
    return await SnapshotStreamOrderLookup.FindFloorSnapshotAsync(connection, streamId, perspectiveName, beforeEventId, ct);
  }

  /// <summary>Removes the stream's tip at or above the late event (#2463); a floor is never removed.</summary>
  /// <docs>fundamentals/events/ephemeral-events#the-tip-snapshot</docs>
  public async Task DeleteTipSnapshotFromAsync(Guid streamId, string perspectiveName, Guid eventId, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);
    await TipSnapshotSql.DeleteTipFromAsync(connection, streamId, perspectiveName, eventId, ct);
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
