// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2294: the ephemeral history of a row its perspective purged (<c>ApplyResult.Purge()</c>) is reclaimed. A purged
/// row has nothing to snapshot, so the coverage gate, which waits for a snapshot at or past each body, held every
/// body of the stream for good, and the reap-driven pass picked the same pairs again on every cycle. A purge marker
/// for the perspective now covers the stream's bodies at or before its purge event, the pass no longer selects a pair
/// it covers, and the maintenance cycle removes the purged row's cursor and snapshots, which describe a row that no
/// longer exists. Bodies after the purge stay gated: a crash can leave a marker on a resurrected, live row.
/// </summary>
/// <remarks>
/// An operator purge marks a stream for every perspective (<c>'*'</c>), and an Apply that resurrects a perspective's
/// row clears only that perspective's marker, so a <c>'*'</c> marker can stand beside a live row. It is never read as
/// coverage, and its stream's cursors and snapshots are never removed by the sweep.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#a-purged-row-covers-its-history</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/403_EphemeralHistoryFollowsWhatAReadModelConsumes.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorCatalogSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralDestructionSql.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class PurgedRowEphemeralHistorySqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Whizbang.Tests.PurgedRowSessionPinged";
  private const string PERSPECTIVE = "TestApp.Sessions+PurgedRowPerspective";
  private const string OTHER_PERSPECTIVE = "TestApp.Sessions+OtherPerspective";
  private const string TABLE = "wh_per_purged_row_session";
  private const string MODEL = "TestApp.Sessions+PurgedRowModel";
  private const string SERVICE = "svc-purged-row";

  // ---------------------------------------------------------------------------------------------
  // Coverage: the reap

  [Test]
  public async Task Reap_TheBodiesOfAPurgedRow_AreReapedWithoutASnapshotAsync() {
    await using var conn = await _openAsync();
    var (stream, eventId) = await _consumedEphemeralEventAsync(conn);
    await _markPurgedAsync(conn, stream, PERSPECTIVE, eventId);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(0L)
      .Because("a row the perspective purged covers its stream's bodies: nothing is left for a snapshot to hold");
  }

  [Test]
  public async Task Reap_AStreamNoPurgeCovers_KeepsItsBodiesUntilASnapshotDoesAsync() {
    await using var conn = await _openAsync();
    var (_, eventId) = await _consumedEphemeralEventAsync(conn);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L)
      .Because("the control: without a purge or a snapshot the coverage gate still holds the body");
  }

  [Test]
  public async Task Reap_AnotherPerspectivesPurge_DoesNotCoverTheBodyAsync() {
    await using var conn = await _openAsync();
    var (stream, eventId) = await _consumedEphemeralEventAsync(conn);
    await _markPurgedAsync(conn, stream, OTHER_PERSPECTIVE, eventId);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L)
      .Because("a purge covers only the bodies of the perspective that purged");
  }

  [Test]
  public async Task Reap_AnOperatorPurgeOfEveryPerspective_DoesNotCoverTheBodyAsync() {
    await using var conn = await _openAsync();
    var (stream, eventId) = await _consumedEphemeralEventAsync(conn);
    await _markPurgedAsync(conn, stream, "*", purgeEventId: null);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L)
      .Because("a '*' marker can stand beside a row a later Apply resurrected, so it says nothing about this row");
  }

  [Test]
  public async Task Reap_AStaleMarkerOnAResurrectedRow_CoversOnlyTheBodiesAtOrBeforeThePurgeAsync() {
    // The runner clears a marker only after it writes the resurrected row, so a crash leaves the marker on a live row.
    // The bodies after the purge feed that live row (a mixed read model replays them from the start), so they stay gated.
    await using var conn = await _openAsync();
    var (stream, before, purge, after) = await _resurrectedStreamWithAStaleMarkerAsync(conn);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, before)).IsEqualTo(0L)
      .Because("a body before the purge belongs to the row's previous life, which no rewind of the resurrected row needs");
    await Assert.That(await _bodyCountAsync(conn, purge)).IsEqualTo(0L).Because("the purge event itself is at the purge");
    await Assert.That(await _bodyCountAsync(conn, after)).IsEqualTo(1L)
      .Because("a body after the purge may feed a resurrected row, so it waits for a snapshot as any other");
    await Assert.That(stream).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task Reap_AMarkerThatNamesNoPurgeEvent_CoversNothingAsync() {
    await using var conn = await _openAsync();
    var (stream, eventId) = await _consumedEphemeralEventAsync(conn);
    await _markPurgedAsync(conn, stream, PERSPECTIVE, purgeEventId: null);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L)
      .Because("without a purge event there is no position the purge is known to have reached, so the gate stays");
  }

  [Test]
  public async Task Reap_AMarkerWhosePurgeEventIsNotInTheStream_CoversNothingAsync() {
    await using var conn = await _openAsync();
    var (stream, eventId) = await _consumedEphemeralEventAsync(conn);
    await _markPurgedAsync(conn, stream, PERSPECTIVE, Guid.CreateVersion7());

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L)
      .Because("a purge event the store cannot place has no version to compare, so it covers nothing");
  }

  [Test]
  public async Task Mirrors_AStaleMarker_LeaveThePostPurgePairATarget_AndOfferOnlyThePrePurgeBodiesAsync() {
    await using var conn = await _openAsync();
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var (stream, before, purge, after) = await _resurrectedStreamWithAStaleMarkerAsync(conn);

    var targets = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();
    var offered = (await coordinator.GetEphemeralBodiesAboutToReapAsync()).Select(t => t.EventId).ToList();

    await Assert.That(targets.Any(t => t.StreamId == stream && t.PerspectiveName == PERSPECTIVE)).IsTrue()
      .Because("the post-purge body is uncovered, so the live row is snapshotted before it can go");
    await Assert.That(offered).Contains(before);
    await Assert.That(offered).Contains(purge);
    await Assert.That(offered).DoesNotContain(after)
      .Because("the destruction hook is offered exactly what the reap deletes");
  }

  // ---------------------------------------------------------------------------------------------
  // Coverage: the reap-driven pass and the destruction hook's batch read the same rule

  [Test]
  public async Task PairsNeedingSnapshot_APurgedPair_IsNotATarget_AndItsBodyIsOfferedForDestructionAsync() {
    await using var conn = await _openAsync();
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var (stream, eventId) = await _consumedEphemeralEventAsync(conn);

    var before = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();
    await Assert.That(before.Any(t => t.StreamId == stream && t.PerspectiveName == PERSPECTIVE)).IsTrue()
      .Because("the control: an uncovered pair with a cursor is a snapshot target");
    await Assert.That((await coordinator.GetEphemeralBodiesAboutToReapAsync()).Any(t => t.EventId == eventId)).IsFalse();

    await _markPurgedAsync(conn, stream, PERSPECTIVE, eventId);

    var after = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();
    await Assert.That(after.Any(t => t.StreamId == stream && t.PerspectiveName == PERSPECTIVE)).IsFalse()
      .Because("#2294: a purged row has nothing to snapshot, so selecting it again every cycle only repeats a no-op");
    await Assert.That((await coordinator.GetEphemeralBodiesAboutToReapAsync()).Any(t => t.EventId == eventId)).IsTrue()
      .Because("the destruction hook is offered exactly what the reap deletes");
  }

  // ---------------------------------------------------------------------------------------------
  // The purge removes the row's cursor and snapshots

  [Test]
  public async Task Maintenance_APurgedRowsCursorAndSnapshots_AreRemoved_AndOtherPerspectivesKeepTheirsAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var stream = Guid.CreateVersion7();
    await _companionsAsync(conn, stream, PERSPECTIVE);
    await _companionsAsync(conn, stream, OTHER_PERSPECTIVE);
    await _markPurgedAsync(conn, stream, PERSPECTIVE, Guid.CreateVersion7());

    await _maintainAsync(conn);

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, PERSPECTIVE)).IsEqualTo(0L)
      .Because("the cursor describes a row that no longer exists");
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, PERSPECTIVE)).IsEqualTo(0L)
      .Because("every snapshot goes, the latest too: unlike an eviction, there is no row to rebuild");
    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, OTHER_PERSPECTIVE)).IsEqualTo(1L);
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, OTHER_PERSPECTIVE)).IsEqualTo(2L)
      .Because("another perspective's state on the same stream is not the purge's to remove");
    await Assert.That(await _countAsync(conn, "wh_stream_purge_markers", stream, PERSPECTIVE)).IsEqualTo(1L)
      .Because("the marker stays: it is what keeps a purged row purged");
  }

  [Test]
  public async Task Maintenance_ALiveRowWithAStaleMarker_KeepsItsCursorAndSnapshotsAsync() {
    // The runner clears the marker only after it writes a resurrected row, so a crash between the two leaves a marker on
    // a live row. The sweep checks the row itself.
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var stream = Guid.CreateVersion7();
    await _rowAsync(conn, stream);
    await _companionsAsync(conn, stream, PERSPECTIVE);
    await _markPurgedAsync(conn, stream, PERSPECTIVE, Guid.CreateVersion7());

    await _maintainAsync(conn);

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, PERSPECTIVE)).IsEqualTo(1L);
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, PERSPECTIVE)).IsEqualTo(2L);
  }

  [Test]
  public async Task Maintenance_APurgedRowWithWorkStillPending_KeepsItsCursorAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    await _associateAsync(conn);
    var stream = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    await _commitAsync(conn, eventId, stream);   // its work item is still unprocessed
    await _companionsAsync(conn, stream, PERSPECTIVE);
    await _markPurgedAsync(conn, stream, PERSPECTIVE, Guid.CreateVersion7());

    await _maintainAsync(conn);

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, PERSPECTIVE)).IsEqualTo(1L)
      .Because("an event still to be applied may resurrect the row; the cursor goes once the stream is settled");
  }

  [Test]
  public async Task Maintenance_APerspectiveNoRegistryRowNames_IsLeftAloneAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var stream = Guid.CreateVersion7();
    const string unregistered = "TestApp.Sessions+UnregisteredPerspective";
    await _companionsAsync(conn, stream, unregistered);
    await _markPurgedAsync(conn, stream, unregistered, Guid.CreateVersion7());

    await _maintainAsync(conn);

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, unregistered)).IsEqualTo(1L)
      .Because("without a registry row the sweep cannot see whether the row is gone, so it guesses nothing");
  }

  [Test]
  public async Task Maintenance_AnOperatorPurgeMarker_RemovesNothingAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var stream = Guid.CreateVersion7();
    await _companionsAsync(conn, stream, PERSPECTIVE);
    await _markPurgedAsync(conn, stream, "*", purgeEventId: null);

    await _maintainAsync(conn);

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, PERSPECTIVE)).IsEqualTo(1L);
  }

  [Test]
  public async Task Maintenance_UnderDebugMode_RemovesNothingAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var stream = Guid.CreateVersion7();
    await _companionsAsync(conn, stream, PERSPECTIVE);
    await _markPurgedAsync(conn, stream, PERSPECTIVE, Guid.CreateVersion7());
    await _executeAsync(conn,
      "INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES ('debug_mode', 'true', 'boolean', 'test') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true'");

    var rows = await _maintainAsync(conn);

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, PERSPECTIVE)).IsEqualTo(1L)
      .Because("debug mode keeps forensic state, as it keeps the bodies");
    await Assert.That(rows.Single(r => r.Task == "remove_purged_row_companions").Status).IsEqualTo("skipped (debug_mode=true)");
  }

  [Test]
  public async Task Maintenance_ReportsHowManyCompanionsItRemovedAsync() {
    await using var conn = await _openAsync();
    await _arrangeTableAsync(conn);
    var stream = Guid.CreateVersion7();
    await _companionsAsync(conn, stream, PERSPECTIVE);
    await _markPurgedAsync(conn, stream, PERSPECTIVE, Guid.CreateVersion7());

    var rows = await _maintainAsync(conn);

    var task = rows.Single(r => r.Task == "remove_purged_row_companions");
    await Assert.That(task.Rows).IsEqualTo(3L).Because("one cursor and two snapshots");
    await Assert.That(task.Status).IsEqualTo("ok");
  }

  // ---------------------------------------------------------------------------------------------
  // Helpers

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // Grace 0 so a consumed body is past its rewind window at once (the window has its own tests).
    await _executeAsync(conn, "UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'ephemeral_rewind_grace_seconds'");
    return conn;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var (name, value) in ps) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<List<(string Task, long Rows, string Status)>> _maintainAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT task_name, rows_affected, status FROM perform_maintenance()", conn);
    var rows = new List<(string, long, string)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add((reader.GetString(0), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture), reader.GetString(2)));
    }
    return rows;
  }

  private static Task _associateAsync(NpgsqlConnection conn) =>
    _executeAsync(conn,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test', @t, NOW(), NOW()) ON CONFLICT DO NOTHING",
      ("t", EVENT_TYPE), ("p", PERSPECTIVE));

  private static Task _commitAsync(NpgsqlConnection conn, Guid eventId, Guid stream) =>
    _executeAsync(conn, "SELECT commit_handler_result(@req::jsonb)", ("req", $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{EVENT_TYPE}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"N": 1}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{stream}}", "IsEvent": true, "Flags": 8
        }]
      }
      """));

  /// <summary>
  /// An ephemeral event the perspective consumed, past its grace, stamped at version 5, with the perspective's cursor
  /// on it and no snapshot: the shape the coverage gate holds.
  /// </summary>
  private static async Task<(Guid Stream, Guid EventId)> _consumedEphemeralEventAsync(NpgsqlConnection conn) {
    await _associateAsync(conn);
    var stream = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    await _commitAsync(conn, eventId, stream);
    await _executeAsync(conn, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @id", ("id", eventId));
    await _executeAsync(conn,
      "UPDATE wh_event_store SET commit_sequence = 5, version = 5, created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id",
      ("id", eventId));
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@s, @p, @e, 1)",
      ("s", stream), ("p", PERSPECTIVE), ("e", eventId));
    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L).Because("the body is offloaded at commit");
    return (stream, eventId);
  }

  /// <summary>
  /// A stream the perspective purged at its second event and then resurrected, with the marker left behind: three
  /// consumed ephemeral events past their grace at versions 5, 6 (the purge) and 7, the perspective's cursor on the
  /// last, no snapshot, and a marker naming the purge event.
  /// </summary>
  private static async Task<(Guid Stream, Guid Before, Guid Purge, Guid After)> _resurrectedStreamWithAStaleMarkerAsync(
      NpgsqlConnection conn) {
    await _associateAsync(conn);
    var stream = Guid.CreateVersion7();
    var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
    for (var i = 0; i < ids.Length; i++) {
      await _commitAsync(conn, ids[i], stream);
    }
    // Versions 5 to 7 sit above the 1 to 3 the commits gave, so the reassignment never collides.
    for (var i = 0; i < ids.Length; i++) {
      await _executeAsync(conn,
        "UPDATE wh_event_store SET version = @v, commit_sequence = @v, created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id",
        ("v", 5 + i), ("id", ids[i]));
      await _executeAsync(conn, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @id", ("id", ids[i]));
    }
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@s, @p, @e, 1)",
      ("s", stream), ("p", PERSPECTIVE), ("e", ids[2]));
    await _markPurgedAsync(conn, stream, PERSPECTIVE, ids[1]);
    return (stream, ids[0], ids[1], ids[2]);
  }

  private static async Task _markPurgedAsync(NpgsqlConnection conn, Guid stream, string perspective, Guid? purgeEventId) {
    await using var cmd = new NpgsqlCommand(
      "INSERT INTO wh_stream_purge_markers (stream_id, perspective_name, purge_event_id) VALUES (@s, @p, @e)", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", perspective);
    cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)purgeEventId ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _bodyCountAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_event_body WHERE event_id = @id", conn);
    cmd.Parameters.AddWithValue("id", eventId);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
      $"INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version) " +
      "VALUES (@id, '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, NOW(), NOW(), 1)",
      ("id", id));

  /// <summary>A cursor and two snapshots of the perspective on the stream.</summary>
  private static async Task _companionsAsync(NpgsqlConnection conn, Guid stream, string perspective) {
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, status) VALUES (@s, @n, 0)",
      ("s", stream), ("n", perspective));
    for (var i = 1; i <= 2; i++) {
      await _executeAsync(conn,
        "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
        "VALUES (@s, @n, @e, '{}'::jsonb, @q, @v)",
        ("s", stream), ("n", perspective), ("e", Guid.CreateVersion7()), ("q", (long)i), ("v", i * 10));
    }
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string table, Guid stream, string perspective) {
    await using var cmd = new NpgsqlCommand(
      $"SELECT COUNT(*) FROM {table} WHERE stream_id = @s AND perspective_name = @n", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("n", perspective);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }
}
