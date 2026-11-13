// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2295: the coverage gate follows what a read model consumes. A perspective that applies only ephemeral events and
/// does not opt in keeps no rewind floor; the startup associations step registers a <c>no_rewind_floor</c> marker for
/// it, and the reaper reaps its bodies once consumed and past grace and TTL, without a snapshot. A perspective with no
/// marker (one that applies a durable event too, one that opted in, or a database no startup of this version
/// registered) keeps the gate.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#snapshots-follow-what-a-read-model-consumes</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/403_EphemeralHistoryFollowsWhatAReadModelConsumes.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorCatalogSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralDestructionSql.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class EphemeralOnlyReadModelReapSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Whizbang.Tests.PresencePinged";
  private const string NO_FLOOR = "TestApp.Presence+NoFloorPerspective";
  private const string KEEPS_FLOOR = "TestApp.Presence+KeepsFloorPerspective";

  [Test]
  public async Task Reap_TheBodyOfAReadModelThatKeepsNoFloor_IsReapedWithoutASnapshotAsync() {
    await using var conn = await _openAsync();
    await _associateAsync(conn, NO_FLOOR);
    await _markNoFloorAsync(conn, NO_FLOOR);
    var eventId = await _consumedEphemeralEventAsync(conn);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(0L)
      .Because("a read model that keeps no rewind floor never takes the snapshot the coverage gate waits for");
  }

  [Test]
  public async Task Reap_TheSameReadModelWithoutTheMarker_HoldsTheBodyAsync() {
    await using var conn = await _openAsync();
    await _associateAsync(conn, NO_FLOOR);
    var eventId = await _consumedEphemeralEventAsync(conn);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L)
      .Because("the control, and the safe default: without the marker the gate holds the body until a snapshot covers it");
  }

  [Test]
  public async Task Reap_ABodyAnotherConsumerStillNeedsCoveredFor_IsHeldAsync() {
    await using var conn = await _openAsync();
    await _associateAsync(conn, NO_FLOOR);
    await _associateAsync(conn, KEEPS_FLOOR);
    await _markNoFloorAsync(conn, NO_FLOOR);
    var eventId = await _consumedEphemeralEventAsync(conn);

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L)
      .Because("the marker exempts only its own perspective; one that keeps a floor still waits for its snapshot");
  }

  [Test]
  public async Task Reap_TheMarkerDoesNotSkipTheOtherGates_ABodyInItsGraceIsKeptAsync() {
    await using var conn = await _openAsync();
    await _associateAsync(conn, NO_FLOOR);
    await _markNoFloorAsync(conn, NO_FLOOR);
    var eventId = await _consumedEphemeralEventAsync(conn);
    await _executeAsync(conn, "UPDATE wh_settings SET setting_value = '3600' WHERE setting_key = 'ephemeral_rewind_grace_seconds'");

    await _maintainAsync(conn);

    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L)
      .Because("no coverage gate is not no gate: grace, TTL, consumption and holds still apply");
  }

  [Test]
  public async Task PairsNeedingSnapshot_AReadModelThatKeepsNoFloor_IsNotATarget_AndItsBodyIsOfferedForDestructionAsync() {
    await using var conn = await _openAsync();
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    await _associateAsync(conn, NO_FLOOR);
    var eventId = await _consumedEphemeralEventAsync(conn);
    await Assert.That((await coordinator.GetEphemeralPairsNeedingSnapshotAsync()).Any(t => t.PerspectiveName == NO_FLOOR)).IsTrue()
      .Because("the control: without the marker the pair is a snapshot target");

    await _markNoFloorAsync(conn, NO_FLOOR);

    await Assert.That((await coordinator.GetEphemeralPairsNeedingSnapshotAsync()).Any(t => t.PerspectiveName == NO_FLOOR)).IsFalse()
      .Because("a read model that keeps no floor is never bootstrapped for the reap");
    await Assert.That((await coordinator.GetEphemeralBodiesAboutToReapAsync()).Any(t => t.EventId == eventId)).IsTrue()
      .Because("the destruction hook is offered exactly what the reap deletes");
  }

  // ---------------------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
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

  private static async Task _maintainAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT * FROM perform_maintenance()", conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain the task rows */ }
  }

  private static Task _associateAsync(NpgsqlConnection conn, string perspective) =>
    _executeAsync(conn,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test', @t, NOW(), NOW()) ON CONFLICT DO NOTHING",
      ("t", EVENT_TYPE), ("p", perspective));

  /// <summary>The marker as the startup step registers it, through <c>register_message_associations</c>.</summary>
  private static Task _markNoFloorAsync(NpgsqlConnection conn, string perspective) =>
    _executeAsync(conn,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (gen_random_uuid(), @p, @a, @p, 'test', @p, NOW(), NOW()) ON CONFLICT DO NOTHING",
      ("p", perspective), ("a", Whizbang.Core.Perspectives.EphemeralRewindFloor.NO_FLOOR_ASSOCIATION_TYPE));

  /// <summary>An ephemeral event every consumer processed, past its grace, stamped at version 5, with cursors and no snapshot.</summary>
  private static async Task<Guid> _consumedEphemeralEventAsync(NpgsqlConnection conn) {
    var stream = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    await _executeAsync(conn, "SELECT commit_handler_result(@req::jsonb)", ("req", $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{EVENT_TYPE}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"N": 1}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{stream}}", "IsEvent": true, "Flags": 8
        }]
      }
      """));
    await _executeAsync(conn, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @id", ("id", eventId));
    await _executeAsync(conn,
      "UPDATE wh_event_store SET commit_sequence = 5, version = 5, created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id",
      ("id", eventId));
    foreach (var perspective in new[] { NO_FLOOR, KEEPS_FLOOR }) {
      await _executeAsync(conn,
        "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@s, @p, @e, 1)",
        ("s", stream), ("p", perspective), ("e", eventId));
    }
    await Assert.That(await _bodyCountAsync(conn, eventId)).IsEqualTo(1L).Because("the body is offloaded at commit");
    return eventId;
  }

  private static async Task<long> _bodyCountAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_event_body WHERE event_id = @id", conn);
    cmd.Parameters.AddWithValue("id", eventId);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }
}
