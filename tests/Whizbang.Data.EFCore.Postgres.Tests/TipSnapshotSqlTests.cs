// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2463, the database's half of the tip snapshot. A perspective that applies ephemeral and durable events keeps one tip
/// snapshot beside its reap-boundary floor. The tip sits near the cursor and its cadence replaces it as the cursor moves,
/// so it never covers a body for the reap, even once its own event is past the grace window: the reap, the reap-driven
/// snapshot targets and the destruction batch all count floors alone. And a cursor flagged for a late event drops the
/// tip at or above it, which was taken without the late event, on every path that flags a rewind.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#the-tip-snapshot</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/620_MixedPerspectivesKeepATipSnapshotBesideTheirFloor.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralSnapshotFloorSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorCatalogSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralDestructionSql.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class TipSnapshotSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Whizbang.Tests.TipSnapshotPinged";
  private const string PERSPECTIVE = "TestApp.Presence+TipSnapshotPerspective";

  [Test]
  public async Task Reap_BodiesBelowATipWhoseEventIsPastItsGrace_AreHeldAsync() {
    await using var conn = await _openAsync();
    var (stream, ids) = await _streamWithASnapshotAtTheTopAsync(conn, isTip: true);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, ids[0])).IsEqualTo(1L)
      .Because("a tip is no floor: once it moved on, a late event between it and the floor would have nothing below it");
    await Assert.That(await _bodyCountAsync(conn, ids[1])).IsEqualTo(1L);
    await Assert.That(await _snapshotCountAsync(conn, stream)).IsEqualTo(1L).Because("the control: the tip is there");
  }

  [Test]
  public async Task Reap_TheSameSnapshotAsAFloor_CoversTheBodiesBelowItAsync() {
    await using var conn = await _openAsync();
    var (_, ids) = await _streamWithASnapshotAtTheTopAsync(conn, isTip: false);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, ids[0])).IsEqualTo(0L)
      .Because("the control: everything but the snapshot's kind is the same, and a floor past its grace covers");
    await Assert.That(await _bodyCountAsync(conn, ids[1])).IsEqualTo(0L);
  }

  [Test]
  public async Task PairsNeedingSnapshot_APairCoveredOnlyByATip_IsATargetAsync() {
    await using var conn = await _openAsync();
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var (stream, ids) = await _streamWithASnapshotAtTheTopAsync(conn, isTip: true);

    var target = (await coordinator.GetEphemeralPairsNeedingSnapshotAsync())
      .SingleOrDefault(t => t.StreamId == stream && t.PerspectiveName == PERSPECTIVE);

    await Assert.That(target).IsNotNull().Because("the tip covers nothing, so the bootstrap must write the floor");
    await Assert.That(target!.BoundaryEventId).IsEqualTo(ids[2]).Because("every body is past its grace, the tip's too");
  }

  [Test]
  public async Task AboutToReap_BodiesCoveredOnlyByATip_AreNotOfferedAsync() {
    await using var conn = await _openAsync();
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var (_, ids) = await _streamWithASnapshotAtTheTopAsync(conn, isTip: true);

    var offered = (await coordinator.GetEphemeralBodiesAboutToReapAsync()).Select(t => t.EventId).ToHashSet();

    await Assert.That(offered.Overlaps(ids)).IsFalse()
      .Because("the destruction batch mirrors the reap: a body the reap keeps is not reported destroyed");
  }

  [Test]
  public async Task ACursorFlaggedForALateEvent_DropsTheTipAtOrAboveIt_AndKeepsTheFloorAsync() {
    await using var conn = await _openAsync();
    var (stream, ids) = await _streamWithASnapshotAtTheTopAsync(conn, isTip: true);
    await _snapshotAsync(conn, stream, ids[0], version: 5, sequence: 2, isTip: false);

    await _executeAsync(conn,
      "UPDATE wh_perspective_cursors SET status = status | 32, rewind_trigger_event_id = @e WHERE stream_id = @s AND perspective_name = @p",
      ("e", ids[1]), ("s", stream), ("p", PERSPECTIVE));

    await Assert.That(await _kindsAsync(conn, stream)).IsEquivalentTo(["5:floor"])
      .Because("the tip at version 7 was taken without the late event at version 6; the floor below it serves the rewind");
  }

  [Test]
  public async Task ACursorFlaggedForALateEventAboveTheTip_KeepsTheTipAsync() {
    await using var conn = await _openAsync();
    var (stream, ids) = await _streamWithASnapshotAtTheTopAsync(conn, isTip: true);
    var later = await _eventAsync(conn, stream, version: 8);

    await _executeAsync(conn,
      "UPDATE wh_perspective_cursors SET rewind_trigger_event_id = @e WHERE stream_id = @s AND perspective_name = @p",
      ("e", later), ("s", stream), ("p", PERSPECTIVE));
    await _executeAsync(conn,
      "UPDATE wh_perspective_cursors SET rewind_trigger_event_id = @e WHERE stream_id = @s AND perspective_name = @p",
      ("e", later), ("s", stream), ("p", PERSPECTIVE));

    await Assert.That(await _kindsAsync(conn, stream)).IsEquivalentTo(["7:tip"])
      .Because("a tip below the late event holds every event before it, so the rewind may restore it");
    await Assert.That(ids).Count().IsEqualTo(3);
  }

  [Test]
  public async Task ACursorWrittenWithALateEvent_DropsTheTipAsync() {
    await using var conn = await _openAsync();
    var (stream, ids) = await _streamWithASnapshotAtTheTopAsync(conn, isTip: true);
    await _executeAsync(conn, "DELETE FROM wh_perspective_cursors WHERE stream_id = @s", ("s", stream));
    await Assert.That(await _kindsAsync(conn, stream)).IsEquivalentTo(["7:tip"]).Because("the control: removing a cursor drops nothing");

    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id) VALUES (@s, @p, @e, 33, @t)",
      ("s", stream), ("p", PERSPECTIVE), ("e", ids[2]), ("t", ids[0]));

    await Assert.That(await _kindsAsync(conn, stream)).IsEmpty();
  }

  // ---------------------------------------------------------------------------------------------

  /// <summary>
  /// Three consumed ephemeral events at versions 5, 6 and 7, every one past its grace window, the perspective's cursor
  /// on the last, and one snapshot at the last, a tip or a floor.
  /// </summary>
  private static async Task<(Guid Stream, Guid[] Ids)> _streamWithASnapshotAtTheTopAsync(NpgsqlConnection conn, bool isTip) {
    await _executeAsync(conn,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test', @t, NOW(), NOW()) ON CONFLICT DO NOTHING",
      ("t", EVENT_TYPE), ("p", PERSPECTIVE));
    var stream = Guid.CreateVersion7();
    var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
    foreach (var id in ids) {
      await _commitAsync(conn, id, stream);
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
    await _snapshotAsync(conn, stream, ids[2], version: 7, sequence: 1, isTip);
    return (stream, ids);
  }

  private static Task _snapshotAsync(NpgsqlConnection conn, Guid stream, Guid at, int version, int sequence, bool isTip) =>
    _executeAsync(conn,
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_commit_sequence, snapshot_version, is_tip) " +
      "VALUES (@s, @p, @e, '{}'::jsonb, @q, @v, @v, @tip)",
      ("s", stream), ("p", PERSPECTIVE), ("e", at), ("q", sequence), ("v", version), ("tip", isTip));

  // A durable event of the stream at the version given, for a late event above everything else.
  private static async Task<Guid> _eventAsync(NpgsqlConnection conn, Guid stream, int version) {
    var id = Guid.CreateVersion7();
    await _executeAsync(conn,
      "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence) " +
      "VALUES (@id, @s, @s, 'TipSnapshotAggregate', @t, @v, @v)",
      ("id", id), ("s", stream), ("t", EVENT_TYPE), ("v", version));
    return id;
  }

  // Each snapshot of the stream as "version:kind".
  private static async Task<List<string>> _kindsAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = new NpgsqlCommand(
      "SELECT snapshot_version, is_tip FROM wh_perspective_snapshots WHERE stream_id = @s ORDER BY snapshot_version", conn);
    cmd.Parameters.AddWithValue("s", stream);
    var kinds = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      kinds.Add(string.Create(CultureInfo.InvariantCulture, $"{reader.GetInt32(0)}:{(reader.GetBoolean(1) ? "tip" : "floor")}"));
    }
    return kinds;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // Grace 0 so a body is past its window unless the test stamps its event in the future on the database's clock.
    await _executeAsync(conn, "UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'ephemeral_rewind_grace_seconds'");
    return conn;
  }

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

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var (name, value) in ps) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _maintainAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT task_name, status FROM perform_maintenance()", conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      await Assert.That(reader.GetString(1)).DoesNotStartWith("failed").Because(reader.GetString(0));
    }
  }

  private static async Task<long> _bodyCountAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_event_body WHERE event_id = @id", conn);
    cmd.Parameters.AddWithValue("id", eventId);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<long> _snapshotCountAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = @s", conn);
    cmd.Parameters.AddWithValue("s", stream);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }
}
