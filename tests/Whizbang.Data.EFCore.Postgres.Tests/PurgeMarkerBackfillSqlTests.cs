// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2314: a row purged before purge markers existed has no marker, so the companion cleanup (#2294) never selects it and
/// its cursor and snapshots are kept forever. The backfill writes the missing marker, naming the cursor's last event, but
/// only where that event's type can only purge on the perspective (the generator's terminal-purge rule). A missing row
/// can also be a row never created (every Apply returned no model), a pre-#1483 eviction, or a row whose stream still has
/// work: a marker on any of them makes the runner skip the stream's later events, so none is ever marked. The rows left
/// unmarked are counted so they are visible.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresPerspectivePurgeMarkerStore.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class PurgeMarkerBackfillSqlTests : EFCoreTestBase {
  private const string PURGING_TYPE = "Shop.Contracts.CartAbandoned, Shop.Contracts";
  private const string CONDITIONAL_TYPE = "Shop.Contracts.CartExpired, Shop.Contracts";
  private const string CREATED_TYPE = "Shop.Contracts.CartOpened, Shop.Contracts";
  private const string PERSPECTIVE = "Shop.Carts+BackfillCartView";
  private const string TABLE = "wh_per_backfill_cart";
  private const string MODEL = "Shop.Carts+BackfillCartModel";
  private const string SERVICE = "svc-purge-backfill";

  private static readonly (string, string)[] _purging = [(PERSPECTIVE, PURGING_TYPE)];

  [Test]
  public async Task Backfill_AMissingRowWhoseLastEventCanOnlyPurge_IsMarkedWithThatEvent_AndItsCompanionsThenGoAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var (stream, last) = await _cursorOnAsync(conn, PURGING_TYPE);
    await _snapshotAsync(conn, stream);

    var marked = await _store().BackfillMarkersAsync(_purging, 5000);

    await Assert.That(marked).IsEqualTo(1);
    await Assert.That(await _markerEventAsync(conn, stream, PERSPECTIVE)).IsEqualTo(last)
      .Because("the marker names the purge, so it covers the bodies at or before it and no later ones");
    await _maintainAsync(conn);
    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream)).IsEqualTo(0L)
      .Because("the existing companion cleanup now selects the pair");
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream)).IsEqualTo(0L);
  }

  [Test]
  public async Task Backfill_ALastEventThatPurgesOnlyConditionally_IsNotMarkedAsync() {
    // The Apply of CartExpired may purge or may keep the row; the generator does not list it, so a missing row after it
    // proves nothing, and a wrong marker would skip the stream's later events.
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var (stream, _) = await _cursorOnAsync(conn, CONDITIONAL_TYPE);

    var marked = await _store().BackfillMarkersAsync(_purging, 5000);

    await Assert.That(marked).IsEqualTo(0);
    await Assert.That(await _countAsync(conn, "wh_stream_purge_markers", stream)).IsEqualTo(0L);
  }

  [Test]
  public async Task Backfill_ARowThatWasNeverCreated_IsNotMarkedAsync() {
    // Every Apply returned no model, yet the run still wrote a cursor: no row, and no purge. A marker would make the
    // runner skip the event that would have created the row.
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var (stream, _) = await _cursorOnAsync(conn, CREATED_TYPE);

    var marked = await _store().BackfillMarkersAsync(_purging, 5000);

    await Assert.That(marked).IsEqualTo(0);
    await Assert.That(await _countAsync(conn, "wh_stream_purge_markers", stream)).IsEqualTo(0L);
  }

  [Test]
  public async Task Backfill_ALiveRow_APendingStream_AndAnUnregisteredPerspective_AreNotMarkedAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var (live, _) = await _cursorOnAsync(conn, PURGING_TYPE);
    await _rowAsync(conn, live);
    var (pending, pendingEvent) = await _cursorOnAsync(conn, PURGING_TYPE);
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at) "
      + "VALUES (@w, @s, @p, @e, 0, 0, NOW())",
      ("w", (Guid)TrackedGuid.New()), ("s", pending), ("p", PERSPECTIVE), ("e", pendingEvent));
    const string unregistered = "Shop.Carts+UnregisteredView";
    var (orphan, _) = await _cursorOnAsync(conn, PURGING_TYPE, unregistered);

    var marked = await _store().BackfillMarkersAsync([.. _purging, (unregistered, PURGING_TYPE)], 5000);

    await Assert.That(marked).IsEqualTo(0);
    await Assert.That(await _countAsync(conn, "wh_stream_purge_markers", live)).IsEqualTo(0L).Because("the row is there");
    await Assert.That(await _countAsync(conn, "wh_stream_purge_markers", pending)).IsEqualTo(0L)
      .Because("an event still to apply may resurrect the row; the stream is not finished");
    await Assert.That(await _countAsync(conn, "wh_stream_purge_markers", orphan)).IsEqualTo(0L)
      .Because("no registry row names the perspective, so nothing can see whether its row is gone");
  }

  [Test]
  public async Task Backfill_AnExistingMarker_AndAnOperatorMarker_AreLeftAsTheyAreAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var (own, _) = await _cursorOnAsync(conn, PURGING_TYPE);
    var ownPurge = (Guid)TrackedGuid.New();
    await _markAsync(conn, own, PERSPECTIVE, ownPurge);
    var (operatorPurged, _) = await _cursorOnAsync(conn, PURGING_TYPE);
    await _markAsync(conn, operatorPurged, "*", null);

    var marked = await _store().BackfillMarkersAsync(_purging, 5000);

    await Assert.That(marked).IsEqualTo(0);
    await Assert.That(await _markerEventAsync(conn, own, PERSPECTIVE)).IsEqualTo(ownPurge);
    await Assert.That(await _countAsync(conn, "wh_stream_purge_markers", operatorPurged)).IsEqualTo(1L)
      .Because("an operator purge already marks the stream for every perspective");
  }

  [Test]
  public async Task Backfill_ExaminesAtMostTheBatchSize_AndTheRestOnALaterCallAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    for (var i = 0; i < 3; i++) {
      _ = await _cursorOnAsync(conn, PURGING_TYPE);
    }

    await Assert.That(await _store().BackfillMarkersAsync(_purging, 2)).IsEqualTo(2);
    await Assert.That(await _store().BackfillMarkersAsync(_purging, 2)).IsEqualTo(1);
    await Assert.That(await _store().BackfillMarkersAsync(_purging, 2)).IsEqualTo(0);
  }

  [Test]
  public async Task CountUnmarked_CountsOnlyTheMissingRowsTheBackfillCannotProve_UpToTheCapAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    _ = await _cursorOnAsync(conn, CONDITIONAL_TYPE);
    _ = await _cursorOnAsync(conn, CREATED_TYPE);
    _ = await _cursorOnAsync(conn, PURGING_TYPE);
    var (live, _) = await _cursorOnAsync(conn, CREATED_TYPE);
    await _rowAsync(conn, live);

    await Assert.That(await _store().CountUnmarkedRowlessPairsAsync(_purging, 100)).IsEqualTo(2L)
      .Because("the purging pair is the backfill's to mark, and the live row is not missing");
    await Assert.That(await _store().CountUnmarkedRowlessPairsAsync(_purging, 1)).IsEqualTo(1L);
  }

  // ---------------------------------------------------------------------------------------------

  private IPerspectivePurgeMarkerStore _store() =>
    new PostgresPerspectivePurgeMarkerStore(ct => _openStoreConnectionAsync(ct));

  private async ValueTask<NpgsqlConnection> _openStoreConnectionAsync(CancellationToken cancellationToken) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    return conn;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var (name, value) in ps) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _maintainAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT task_name FROM perform_maintenance()", conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // drained so the whole cycle runs
    }
  }

  /// <summary>One event of the type on a new stream, and the perspective's cursor on it with no work pending.</summary>
  private static async Task<(Guid Stream, Guid Event)> _cursorOnAsync(
      NpgsqlConnection conn, string eventType, string perspective = PERSPECTIVE) {
    var stream = (Guid)TrackedGuid.New();
    var eventId = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at) "
      + "VALUES (@e, @s, @s, 'Cart', @t, 1, NOW())",
      ("e", eventId), ("s", stream), ("t", eventType));
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@s, @p, @e, 2, NOW())",
      ("s", stream), ("p", perspective), ("e", eventId));
    return (stream, eventId);
  }

  private static Task _snapshotAsync(NpgsqlConnection conn, Guid stream) =>
    _executeAsync(conn,
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) "
      + "VALUES (@s, @p, @e, '{}'::jsonb, 1, 1)",
      ("s", stream), ("p", PERSPECTIVE), ("e", (Guid)TrackedGuid.New()));

  private static async Task _markAsync(NpgsqlConnection conn, Guid stream, string perspective, Guid? purgeEventId) {
    await using var cmd = new NpgsqlCommand(
      "INSERT INTO wh_stream_purge_markers (stream_id, perspective_name, purge_event_id) VALUES (@s, @p, @e)", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", perspective);
    cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)purgeEventId ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Guid?> _markerEventAsync(NpgsqlConnection conn, Guid stream, string perspective) {
    await using var cmd = new NpgsqlCommand(
      "SELECT purge_event_id FROM wh_stream_purge_markers WHERE stream_id = @s AND perspective_name = @p", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", perspective);
    return await cmd.ExecuteScalarAsync() as Guid?;
  }

  /// <summary>The perspective table and the registry row that names <see cref="PERSPECTIVE"/> as folding into it.</summary>
  private static async Task _arrangeTableAsync(NpgsqlConnection conn) {
    await _executeAsync(conn, $@"
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      DELETE FROM wh_perspective_registry WHERE table_name = '{TABLE}';");
    await _executeAsync(conn, "SELECT * FROM reconcile_perspective_registry(@p::jsonb, @svc)",
      ("p", $$"""[{"ClrTypeName":"{{MODEL}}","TableName":"{{TABLE}}","SchemaJson":{},"SchemaHash":"h","ServiceName":"{{SERVICE}}","PerspectiveName":"{{PERSPECTIVE}}"}]"""),
      ("svc", SERVICE));
  }

  private static Task _rowAsync(NpgsqlConnection conn, Guid id) =>
    _executeAsync(conn,
      $"INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version) "
      + "VALUES (@id, '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, NOW(), NOW(), 1)",
      ("id", id));

  private static async Task<long> _countAsync(NpgsqlConnection conn, string table, Guid stream) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {table} WHERE stream_id = @s", conn);
    cmd.Parameters.AddWithValue("s", stream);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }
}
