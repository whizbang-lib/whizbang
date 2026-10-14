// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Picks the snapshot a rewind restores from: the latest one before the triggering event in the stream's
/// own order. Shared by both Postgres drivers, which differ in how they get a connection and in nothing
/// else that matters here.
/// </summary>
/// <remarks>
/// <para>
/// A rewind restores the model from a snapshot and replays forward from it, so the snapshot has to sit
/// before the triggering event in the stream. Commit sequence is assignment order, and a row stamped after
/// the fact carries a sequence far below rows written long before it, so within one stream the two orders
/// can run opposite to each other. Event id is a UUIDv7 ordered by generation time, which races the same
/// way. Version is the stream's own counter, unique per stream by index, and cannot invert (#1355).
/// </para>
/// <para>
/// Living in one place rather than once per driver is the point: two copies of this drifted apart would
/// mean a rewind restoring from a different snapshot depending on which driver a consumer configured, and
/// nothing would report the difference.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/snapshots</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SnapshotSelectionFollowsStreamOrderTests.cs:GetLatestSnapshotBeforeEventInStreamOrder_SnapshotLaterInTheStream_IsNotSelectedAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperPerspectiveSnapshotStoreTests.cs:GetLatestSnapshotBeforeEventInStreamOrder_EventTheStoreCannotPlace_FallsBackToEventIdOrderAsync</tests>
public static class SnapshotStreamOrderLookup {
  /// <summary>Resolves where the triggering event sits in its stream. Null when this store holds no row for it.</summary>
  private const string VERSION_SQL = """
    SELECT es.version FROM wh_event_store es WHERE es.event_id = @p_before_event_id
    """;

  /// <summary>
  /// The event is placed, so version order is available and is the only order worth trusting. A snapshot
  /// recording no version predates the column; nothing can order it against this event, so it is refused
  /// rather than guessed at. That costs a fuller replay, which reproduces the row, where the opposite
  /// guess skips the events in between and leaves nothing pending to repair them.
  /// </summary>
  private const string BY_VERSION_SQL = """
    SELECT s.snapshot_event_id, s.snapshot_version, s.snapshot_data
    FROM wh_perspective_snapshots s
    WHERE s.stream_id = @p_stream_id
      AND s.perspective_name = @p_perspective_name
      AND s.snapshot_version IS NOT NULL
      AND s.snapshot_version < @p_before_version
    ORDER BY s.snapshot_version DESC
    LIMIT 1
    """;

  /// <summary>
  /// The event is not placed, which means this store keeps no version for it. A store with no versions has
  /// no retroactive stamping to invert against them either, so event id order is the only order it has and
  /// is as sound here as it ever was. Refusing would replay every rewind from event zero for a store that
  /// never had the defect.
  /// </summary>
  private const string BY_EVENT_ID_SQL = """
    SELECT s.snapshot_event_id, s.snapshot_version, s.snapshot_data
    FROM wh_perspective_snapshots s
    WHERE s.stream_id = @p_stream_id
      AND s.perspective_name = @p_perspective_name
      AND s.snapshot_event_id < @p_before_event_id
    ORDER BY s.snapshot_event_id DESC
    LIMIT 1
    """;

  /// <summary>
  /// Returns the latest snapshot before <paramref name="beforeEventId"/> in the stream's order, on an OPEN
  /// connection. Null when there is none, which a caller reads as "replay further" rather than as an error.
  /// </summary>
  public static async Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> FindAsync(
      NpgsqlConnection connection,
      Guid streamId,
      string perspectiveName,
      Guid beforeEventId,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(connection);

    // Resolved first, because the answer decides which order can be trusted. Two statements rather than one
    // branching query: a rewind is rare and the reader of this code is not.
    int? triggerVersion;
    await using (var versionCmd = new NpgsqlCommand(VERSION_SQL, connection)) {
      versionCmd.Parameters.AddWithValue("p_before_event_id", beforeEventId);
      var raw = await versionCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
      triggerVersion = raw is null or DBNull
        ? null
        : Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    await using var cmd = new NpgsqlCommand(triggerVersion.HasValue ? BY_VERSION_SQL : BY_EVENT_ID_SQL, connection);
    cmd.Parameters.AddWithValue("p_stream_id", streamId);
    cmd.Parameters.AddWithValue("p_perspective_name", perspectiveName);
    if (triggerVersion.HasValue) {
      cmd.Parameters.AddWithValue("p_before_version", triggerVersion.Value);
    } else {
      cmd.Parameters.AddWithValue("p_before_event_id", beforeEventId);
    }

    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    if (!await reader.ReadAsync(ct).ConfigureAwait(false)) {
      return null;
    }
    var snapshotEventId = reader.GetGuid(0);
    int? snapshotVersion = await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetInt32(1);
    var snapshotJson = reader.GetString(2);
    return (snapshotEventId, snapshotVersion, JsonDocument.Parse(snapshotJson));
  }
}
