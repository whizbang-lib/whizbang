using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Reconcile self-declares the rewind at WORK-ITEM CREATION. A backfilled event keeps its
/// ORIGINAL (older) event id, so the moment the inbox emit chain creates its perspective work
/// item the system already knows the event slots BELOW the cursor — no heuristic detection is
/// possible later (the backfill's local commit_sequence is fresh and above the cursor, so the
/// inversion detector can never see it). The emit chain therefore flags the cursor
/// RewindRequired with the straggler as trigger, and the worker's existing rewind routing does
/// the rest. The pre-existing straggler check in complete_perspective_checkpoint runs only at
/// completion time and only catches events the runner never saw — a reconciled event is seen,
/// applied in arrival order, and marked processed, so that check stays silent.
/// </summary>
/// <docs>fundamentals/perspectives/rewind-invariants</docs>
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

  private static async Task _driveInboxEventAsync(NpgsqlConnection conn, Guid streamId, Guid eventId) {
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
        "SourceServiceId": "{{Guid.NewGuid()}}",
        "SourceCommitSequence": 7
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
      lease.CommandText = "UPDATE wh_inbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @id";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("id", eventId);
      await lease.ExecuteNonQueryAsync();
    }
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
    }
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
    return (r.GetInt32(0), r.IsDBNull(1) ? null : r.GetGuid(1));
  }

  [Test]
  public async Task InboxEmitChain_LateEventBelowCursor_DeclaresRewindOnTheCursorAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _registerAssociationAsync(conn);

    var streamId = Guid.NewGuid();
    var cursorEventId = Guid.Parse("019f0000-0000-7000-8000-00000000cccc");   // "today"
    var monthOldEventId = Guid.Parse("019e0000-0000-7000-8000-00000000aaaa"); // origin-era id
    // The cursor's last event must exist (FK) — the stream's normal history, applied and done.
    await _driveInboxEventAsync(conn, streamId, cursorEventId);
    await _seedCursorAsync(conn, streamId, cursorEventId);

    await _driveInboxEventAsync(conn, streamId, monthOldEventId);

    var (status, trigger) = await _cursorAsync(conn, streamId);
    await Assert.That(status & 32).IsEqualTo(32)
      .Because("a work item whose event id slots BELOW the cursor is a straggler by "
               + "construction — reconcile must self-declare the rewind at creation time, "
               + "because the backfill's fresh local commit_sequence makes it invisible to "
               + "every later inversion check");
    await Assert.That(trigger).IsEqualTo(monthOldEventId)
      .Because("the straggler itself is the rewind trigger — the replay floor must sit below it");
  }

  [Test]
  public async Task InboxEmitChain_EventAboveCursor_DoesNotDeclareRewindAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _registerAssociationAsync(conn);

    var streamId = Guid.NewGuid();
    var cursorEventId = Guid.Parse("019e0000-0000-7000-8000-00000000cccc");
    var newerEventId = Guid.Parse("019f0000-0000-7000-8000-00000000eeee");
    await _driveInboxEventAsync(conn, streamId, cursorEventId);
    await _seedCursorAsync(conn, streamId, cursorEventId);

    await _driveInboxEventAsync(conn, streamId, newerEventId);

    var (status, trigger) = await _cursorAsync(conn, streamId);
    await Assert.That(status & 32).IsEqualTo(0)
      .Because("normal forward progress must never declare a rewind — the declaration is "
               + "strictly for events that slot below the cursor");
    await Assert.That(trigger).IsNull();
  }
}
