// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Reconcile self-declares the rewind at WORK-ITEM CREATION. A backfilled event keeps its
/// ORIGINAL (older) event id, so the moment the inbox emit chain creates its perspective work
/// item the system already knows the event slots BELOW the cursor in its origin's order (#2551: never by id) — no heuristic detection is
/// possible later (the backfill's local commit_sequence is fresh and above the cursor, so the
/// inversion detector can never see it). The emit chain therefore flags the cursor
/// RewindRequired with the straggler as trigger, and the worker's existing rewind routing does
/// the rest. The pre-existing straggler check in complete_perspective_checkpoint runs only at
/// completion time and only catches events the runner never saw — a reconciled event is seen,
/// applied in arrival order, and marked processed, so that check stays silent.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#detection</docs>
[Category("Shard4")]
public class ReconcileRewindDeclarationSqlTests : EFCoreTestBase {

  private const string EVENT_TYPE = "Whizbang.Tests.ReconcileRewindProbeEvent";
  private const string PERSPECTIVE = "reconcile_rewind_probe";

  private static async Task<NpgsqlConnection> _openAsync(DbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _registerAssociationAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name,
         normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @messageType, 'perspective', @target, 'test-svc',
              @messageType, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """;
    cmd.Parameters.AddWithValue("messageType", EVENT_TYPE);
    cmd.Parameters.AddWithValue("target", PERSPECTIVE);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _seedCursorAsync(NpgsqlConnection conn, Guid streamId, Guid lastEventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at)
      VALUES (@stream, @name, @last, 2, NOW())
      ON CONFLICT (stream_id, perspective_name) DO UPDATE SET last_event_id = @last, status = 2
      """;
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("name", PERSPECTIVE);
    cmd.Parameters.AddWithValue("last", lastEventId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _driveInboxEventAsync(NpgsqlConnection conn, Guid streamId, Guid eventId, Guid origin, long originSequence) {
    var instanceId = Guid.NewGuid();
    var inbox = $$"""
      [{
        "MessageId": "{{eventId}}",
        "HandlerName": "TestHandler",
        "MessageType": "{{EVENT_TYPE}}",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {"p": {"Probe": 1}, "h": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true,
        "Flags": 0,
        "SourceServiceId": "{{origin}}",
        "SourceCommitSequence": {{originSequence}}
      }]
      """;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      store.Parameters.AddWithValue("p", inbox);
      store.Parameters.AddWithValue("inst", instanceId);
      await using var r = await store.ExecuteReaderAsync();
      while (await r.ReadAsync()) { /* drain */ }
    }
    await using (var lease = conn.CreateCommand()) {
      lease.CommandText = "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @id";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("id", eventId);
      await lease.ExecuteNonQueryAsync();
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
    emit.Parameters.AddWithValue("inst", instanceId);
    _ = await emit.ExecuteScalarAsync();
  }

  private static async Task<(int Status, Guid? Trigger)> _cursorAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT status, rewind_trigger_event_id FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @n";
    cmd.Parameters.AddWithValue("s", streamId);
    cmd.Parameters.AddWithValue("n", PERSPECTIVE);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) {
      return (0, null);
    }
    return (r.GetInt32(0), await r.IsDBNullAsync(1) ? null : r.GetGuid(1));
  }

  [Test]
  public async Task InboxEmitChain_LateEventBelowCursor_DeclaresRewindOnTheCursorAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _registerAssociationAsync(conn);

    var streamId = Guid.NewGuid();
    var origin = Guid.NewGuid();
    // #2551: below the cursor in the origin's order (sequence 3 under the cursor's 5), although its id sorts above the
    // cursor's: ids are time-ordered and never stream position.
    var cursorEventId = Guid.Parse("019e0000-0000-7000-8000-00000000cccc");
    var lateEventId = Guid.Parse("019f0000-0000-7000-8000-00000000aaaa");
    // The cursor's last event must exist (FK) — the stream's normal history, applied and done.
    await _driveInboxEventAsync(conn, streamId, cursorEventId, origin, 5);
    await _seedCursorAsync(conn, streamId, cursorEventId);

    await _driveInboxEventAsync(conn, streamId, lateEventId, origin, 3);

    var (status, trigger) = await _cursorAsync(conn, streamId);
    await Assert.That(status & 32).IsEqualTo(32)
      .Because("a work item its origin committed before the cursor's last event is a straggler by construction: "
               + "reconcile must self-declare the rewind at creation time, because the backfill's fresh local "
               + "commit_sequence makes it invisible to every later inversion check");
    await Assert.That(trigger).IsEqualTo(lateEventId)
      .Because("the straggler itself is the rewind trigger — the replay floor must sit below it");
  }

  [Test]
  public async Task InboxEmitChain_EventAboveCursor_DoesNotDeclareRewindAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _registerAssociationAsync(conn);

    var streamId = Guid.NewGuid();
    var origin = Guid.NewGuid();
    // #2551: after the cursor in the origin's order (sequence 6 over 5), although its id sorts below the cursor's.
    var cursorEventId = Guid.Parse("019f0000-0000-7000-8000-00000000cccc");
    var newerEventId = Guid.Parse("019e0000-0000-7000-8000-00000000eeee");
    await _driveInboxEventAsync(conn, streamId, cursorEventId, origin, 5);
    await _seedCursorAsync(conn, streamId, cursorEventId);

    await _driveInboxEventAsync(conn, streamId, newerEventId, origin, 6);

    var (status, trigger) = await _cursorAsync(conn, streamId);
    await Assert.That(status & 32).IsEqualTo(0)
      .Because("normal forward progress in the origin's order must never declare a rewind, whatever the ids say");
    await Assert.That(trigger).IsNull();
  }

  [Test]
  public async Task InboxEmitChain_AnotherOriginsEvent_WithALowerId_DoesNotDeclareRewindAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _registerAssociationAsync(conn);

    var streamId = Guid.NewGuid();
    var cursorEventId = Guid.Parse("019f0000-0000-7000-8000-00000000cccc");
    var otherOriginsEventId = Guid.Parse("019e0000-0000-7000-8000-00000000dddd");
    await _driveInboxEventAsync(conn, streamId, cursorEventId, Guid.NewGuid(), 5);
    await _seedCursorAsync(conn, streamId, cursorEventId);

    await _driveInboxEventAsync(conn, streamId, otherOriginsEventId, Guid.NewGuid(), 1);

    var (status, trigger) = await _cursorAsync(conn, streamId);
    await Assert.That(status & 32).IsEqualTo(0)
      .Because("a replay keeps the arrival order between origins (#2257), so another origin's event stored after the "
               + "cursor's is after it; its lower id says nothing about stream position");
    await Assert.That(trigger).IsNull();
  }
}
