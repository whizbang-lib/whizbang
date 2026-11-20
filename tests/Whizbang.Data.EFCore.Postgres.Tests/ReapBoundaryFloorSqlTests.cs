// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2456: a snapshot is a rewind floor for the ephemeral bodies below it only once its own event is past the grace
/// window. A snapshot taken at the tip, by the live apply or a rewind, sits above bodies still inside the window, where a
/// late event may yet land. It satisfied the reap's coverage gate all the same, the bodies below it were reaped as they
/// aged, and a late event landing below the tip found no floor. Such a snapshot now covers nothing until its event ages
/// out: the bodies below it stay, the pair is a reap-driven target at its reap boundary, and the destruction batch does
/// not offer them.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#the-reap-boundary-snapshot</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/534_ATipSnapshotIsNoFloorUntilItsEventAgesOut.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralSnapshotFloorSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorCatalogSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralDestructionSql.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class ReapBoundaryFloorSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Whizbang.Tests.ReapBoundaryFloorPinged";
  private const string PERSPECTIVE = "TestApp.Presence+ReapBoundaryFloorPerspective";

  [Test]
  public async Task Reap_BodiesBelowASnapshotWhoseEventIsStillInItsGrace_AreHeldAsync() {
    await using var conn = await _openAsync();
    var (stream, ids) = await _streamWithATipSnapshotAsync(conn);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, ids[0])).IsEqualTo(1L)
      .Because("the snapshot sits above a body still inside its grace window, so it is no floor for a late event landing there");
    await Assert.That(await _bodyCountAsync(conn, ids[1])).IsEqualTo(1L);
    await Assert.That(await _snapshotCountAsync(conn, stream)).IsEqualTo(1L).Because("the control: the snapshot is there");
  }

  [Test]
  public async Task Reap_OnceTheSnapshotsEventIsPastItsGrace_TheBodiesBelowItAreReapedAsync() {
    await using var conn = await _openAsync();
    var (_, ids) = await _streamWithATipSnapshotAsync(conn);
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id", ("id", ids[2]));

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, ids[0])).IsEqualTo(0L)
      .Because("every body at or below the snapshot is past its window, so the snapshot is the floor and the gate releases them");
    await Assert.That(await _bodyCountAsync(conn, ids[1])).IsEqualTo(0L);
  }

  [Test]
  public async Task Reap_ASnapshotWhoseEventTheStoreNoLongerHolds_StillCoversAsync() {
    await using var conn = await _openAsync();
    var (stream, ids) = await _streamWithATipSnapshotAsync(conn);
    await _executeAsync(conn, "UPDATE wh_perspective_snapshots SET snapshot_event_id = @e WHERE stream_id = @s",
      ("e", Guid.CreateVersion7()), ("s", stream));

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, ids[0])).IsEqualTo(0L)
      .Because("an event the store no longer holds is long past its window, so its snapshot covers as before");
  }

  [Test]
  public async Task PairsNeedingSnapshot_APairCoveredOnlyByASnapshotStillInItsGrace_IsATarget_AtTheReapBoundaryAsync() {
    await using var conn = await _openAsync();
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var (stream, ids) = await _streamWithATipSnapshotAsync(conn);

    var target = (await coordinator.GetEphemeralPairsNeedingSnapshotAsync())
      .SingleOrDefault(t => t.StreamId == stream && t.PerspectiveName == PERSPECTIVE);

    await Assert.That(target).IsNotNull()
      .Because("the tip snapshot covers nothing yet, so the bootstrap must write the floor at the reap boundary");
    await Assert.That(target!.BoundaryEventId).IsEqualTo(ids[1])
      .Because("the boundary is the last body past its grace, below the snapshot's event");
  }

  [Test]
  public async Task PairsNeedingSnapshot_APairWhoseSnapshotEventIsPastItsGrace_IsNotATargetAsync() {
    await using var conn = await _openAsync();
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var (stream, ids) = await _streamWithATipSnapshotAsync(conn);
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id", ("id", ids[2]));

    var targets = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();

    await Assert.That(targets.Any(t => t.StreamId == stream)).IsFalse()
      .Because("a snapshot past its window covers the bodies at or below it, as before");
  }

  [Test]
  public async Task AboutToReap_BodiesBelowASnapshotWhoseEventIsStillInItsGrace_AreNotOfferedAsync() {
    await using var conn = await _openAsync();
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var (_, ids) = await _streamWithATipSnapshotAsync(conn);

    var offered = (await coordinator.GetEphemeralBodiesAboutToReapAsync()).Select(t => t.EventId).ToHashSet();

    await Assert.That(offered.Contains(ids[0])).IsFalse()
      .Because("the destruction batch mirrors the reap: a body the reap keeps is not reported destroyed");
    await Assert.That(offered.Contains(ids[1])).IsFalse();
  }

  // ---------------------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // Grace 0 so a body is past its window unless the test stamps its event in the future on the database's clock.
    await _executeAsync(conn, "UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'ephemeral_rewind_grace_seconds'");
    return conn;
  }

  /// <summary>
  /// Three consumed ephemeral events at versions 5, 6 and 7, the perspective's cursor on the last, and one snapshot at
  /// the last: the two below are past their grace window and the last is still inside it (stamped an hour ahead on the
  /// database's clock), the shape a live tip snapshot leaves.
  /// </summary>
  private static async Task<(Guid Stream, Guid[] Ids)> _streamWithATipSnapshotAsync(NpgsqlConnection conn) {
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
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = NOW() + INTERVAL '1 hour' WHERE event_id = @id", ("id", ids[2]));
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@s, @p, @e, 1)",
      ("s", stream), ("p", PERSPECTIVE), ("e", ids[2]));
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_commit_sequence, snapshot_version) " +
      "VALUES (@s, @p, @e, '{}'::jsonb, 1, 7, 7)",
      ("s", stream), ("p", PERSPECTIVE), ("e", ids[2]));
    return (stream, ids);
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
