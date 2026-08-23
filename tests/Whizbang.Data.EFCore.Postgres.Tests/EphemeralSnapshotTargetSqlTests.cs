using System;
using System.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <c>GetEphemeralPairsNeedingSnapshotAsync</c> (E1 #13d/(2-reap)) — the query the
/// maintenance cycle uses to find the (stream, perspective) pairs it must bootstrap-snapshot BEFORE the
/// reaper deletes their consumed, aged ephemeral bodies. A pair qualifies when the body is consumed AND aged
/// past its grace window AND the perspective has NO snapshot at/past the event's commit_sequence; once a
/// covering snapshot exists, the pair drops out. Verified against a real Postgres.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
[Category("Shard3")]
public class EphemeralSnapshotTargetSqlTests : EFCoreTestBase {
  private const string EventType = "Whizbang.Tests.SnapTargetEvent";
  private const string Perspective = "SnapTargetPerspective";

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection connection, string sql, params (string, object)[] ps) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in ps) {
      cmd.Parameters.AddWithValue(n, v);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task GetEphemeralPairsNeedingSnapshot_ReturnsConsumedAgedUncovered_ThenDropsAfterSnapshotAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    // A consuming perspective association so the emit chain creates a perspective work item.
    await _execAsync(connection,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test', @t, NOW(), NOW()) ON CONFLICT DO NOTHING",
      ("t", EventType), ("p", Perspective));

    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{EventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 8
        }]
      }
      """;
    await _execAsync(connection, "SELECT commit_handler_result(@req::jsonb)", ("req", request));

    // Consume (work item processed), stamp commit_sequence, age past grace, and set a cursor.
    await _execAsync(connection, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @id", ("id", eventId));
    await _execAsync(connection, "UPDATE wh_event_store SET commit_sequence = 5 WHERE event_id = @id", ("id", eventId));
    await _execAsync(connection, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id", ("id", eventId));
    await _execAsync(connection,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@sid, @p, @eid, 1)",
      ("sid", streamId), ("p", Perspective), ("eid", eventId));

    // Uncovered => the pair is a snapshot target, carrying the cursor's last event id.
    var pairs = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();
    var target = pairs.FirstOrDefault(t => t.StreamId == streamId && t.PerspectiveName == Perspective);
    await Assert.That(target).IsNotNull().Because("A consumed, aged, uncovered ephemeral pair must be snapshotted before the reap.");
    await Assert.That(target!.LastEventId).IsEqualTo(eventId).Because("It carries the perspective cursor's last processed event to snapshot at.");

    // Cover it with a snapshot at/past the event's commit_sequence.
    await _execAsync(connection,
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_commit_sequence) " +
      "VALUES (@sid, @p, @eid, '{}'::jsonb, 1, 5)",
      ("sid", streamId), ("p", Perspective), ("eid", eventId));

    var pairsAfter = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();
    await Assert.That(pairsAfter.Any(t => t.StreamId == streamId && t.PerspectiveName == Perspective)).IsFalse()
      .Because("Once a covering snapshot exists (snapshot_commit_sequence >= the event), the pair is no longer a target.");
  }

  [Test]
  public async Task GetEphemeralPairsNeedingSnapshot_SourcedBodyRow_IsNotATargetAsync() {
    // #13b4 safety gate. Post-split, SOURCED bodies live in wh_event_body too — the reap-driven snapshot
    // query must be explicitly gated on the event being ephemeral (flags&8), or every consumed sourced
    // event would become a bogus bootstrap-snapshot target.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    const string sourcedType = "Whizbang.Tests.SourcedSnapTarget";
    await _execAsync(connection,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test', @t, NOW(), NOW()) ON CONFLICT DO NOTHING",
      ("t", sourcedType), ("p", Perspective));

    // A SOURCED event (flags 0) committed through the emit chain…
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{sourcedType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 0
        }]
      }
      """;
    await _execAsync(connection, "SELECT commit_handler_result(@req::jsonb)", ("req", request));

    // …whose body lives in wh_event_body from birth (full split, 077); consumed, stamped, aged, with a cursor.
    await _execAsync(connection, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @id", ("id", eventId));
    await _execAsync(connection, "UPDATE wh_event_store SET commit_sequence = 5, created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id", ("id", eventId));
    await _execAsync(connection,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@sid, @p, @eid, 1)",
      ("sid", streamId), ("p", Perspective), ("eid", eventId));

    var pairs = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();
    await Assert.That(pairs.Any(t => t.StreamId == streamId)).IsFalse()
      .Because("A sourced body row is never a reap-driven snapshot target — the query's ephemeral gate (flags&8) excludes it.");
  }

  [Test]
  public async Task GetEphemeralPairsNeedingSnapshot_ExcludesNotYetAgedAndNotConsumedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _execAsync(connection,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test', @t, NOW(), NOW()) ON CONFLICT DO NOTHING",
      ("t", EventType), ("p", Perspective));
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{EventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 8
        }]
      }
      """;
    await _execAsync(connection, "SELECT commit_handler_result(@req::jsonb)", ("req", request));
    await _execAsync(connection, "UPDATE wh_event_store SET commit_sequence = 5 WHERE event_id = @id", ("id", eventId));
    await _execAsync(connection,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@sid, @p, @eid, 1)",
      ("sid", streamId), ("p", Perspective), ("eid", eventId));

    // Recent (not aged) AND its perspective work item is still unprocessed (not consumed) — excluded on both counts.
    var pairs = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();
    await Assert.That(pairs.Any(t => t.StreamId == streamId)).IsFalse()
      .Because("A body that is not yet consumed and not yet aged past grace is not a snapshot target.");
  }
}
