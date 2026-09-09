using System.Data;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the doorbell machinery against row-lock waits (issue #699). The inbox and outbox stores
/// probe "is this stream's queue empty" to ring the empty-to-non-empty doorbell; migration 114 made
/// that probe a locking read so a store racing the completion of the stream's last pending row
/// re-reads after the completion commits instead of missing the edge. The claim tick stamps its
/// notify-state watermark inside the lease transaction. Under a burst the completion is a 50-row
/// batch holding those rows for seconds, hundreds of receivers probe the same hot streams, and the
/// claim tick's upsert lands on the same notify-state rows the store's debounce touches: three
/// statements, three lock orders, deadlocks resolved by 40P01 and commits that take seconds.
/// </summary>
/// <remarks>
/// Each test holds a row lock on one connection inside an open transaction and runs the statement
/// under test on a second connection with a short statement timeout. Today the statement waits on
/// the lock and the timeout fires (57014). Lock-free, it returns at once: a locked pending row is a
/// row being completed, so treating the queue as empty and ringing is the safe side of the race.
/// </remarks>
/// <docs>fundamentals/messaging/inbox</docs>
[Category("Shard3")]
public class DoorbellProbeLockFreeSqlTests : EFCoreTestBase {
  private const int STATEMENT_TIMEOUT_MS = 3000;

  [Test]
  public async Task InboxStore_WhileTheStreamsLastPendingRowIsBeingCompleted_DoesNotWaitAsync() {
    await using var holder = await _openAsync();
    await using var prober = await _openAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var first = (Guid)TrackedGuid.NewMedo();
    await _storeInboxAsync(holder, first, streamId);

    // A commit batch completing the stream's only pending row, still uncommitted.
    await using var tx = await holder.BeginTransactionAsync();
    await _execAsync(holder, "UPDATE wh_inbox SET processed_at = NOW() WHERE message_id = @m", ("m", first), tx);

    await _setStatementTimeoutAsync(prober);
    var second = (Guid)TrackedGuid.NewMedo();
    await _storeInboxAsync(prober, second, streamId);   // must not wait on the held row

    await Assert.That(await _existsAsync(prober, "wh_inbox", "message_id", second)).IsTrue();
    await tx.RollbackAsync();
  }

  [Test]
  public async Task OutboxStore_WhileTheStreamsLastPendingRowIsBeingCompleted_DoesNotWaitAsync() {
    await using var holder = await _openAsync();
    await using var prober = await _openAsync();
    var instance = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var first = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(holder, instance);
    await _storeOutboxAsync(holder, instance, first, streamId);

    await using var tx = await holder.BeginTransactionAsync();
    await _execAsync(holder, "UPDATE wh_outbox SET processed_at = NOW() WHERE message_id = @m", ("m", first), tx);

    await _setStatementTimeoutAsync(prober);
    var second = (Guid)TrackedGuid.NewMedo();
    await _storeOutboxAsync(prober, instance, second, streamId);

    await Assert.That(await _existsAsync(prober, "wh_outbox", "message_id", second)).IsTrue();
    await tx.RollbackAsync();
  }

  [Test]
  public async Task ClaimTick_WhileItsNotifyStateRowsAreHeld_DoesNotWaitAsync() {
    await using var holder = await _openAsync();
    await using var claimer = await _openAsync();
    var instance = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(holder, instance);
    // Pending inbox work so the tick claims something and reaches its watermark stamp.
    await _storeInboxAsync(holder, (Guid)TrackedGuid.NewMedo(), streamId);
    // The watermark rows exist and another session holds them (the store's debounce does exactly this).
    await _execAsync(holder,
      "INSERT INTO wh_notify_state (instance_id, payload_kind, last_work_at) VALUES (@i,'inbox',NOW()),(@i,'outbox',NOW()),(@i,'perspective',NOW()) ON CONFLICT DO NOTHING",
      ("i", instance));
    await using var tx = await holder.BeginTransactionAsync();
    await _execAsync(holder, "SELECT 1 FROM wh_notify_state WHERE instance_id = @i FOR UPDATE", ("i", instance), tx);

    await _setStatementTimeoutAsync(claimer);
    await using var cmd = claimer.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM claim_work(@i, 'test', 'test-host', 1, 10, 1, 300)";
    cmd.Parameters.AddWithValue("i", instance);
    var claimed = (long)(await cmd.ExecuteScalarAsync() ?? 0L);   // must not wait on the held watermark rows

    await Assert.That(claimed).IsGreaterThanOrEqualTo(1).Because("the pending inbox row is claimable");
    await tx.RollbackAsync();
  }

  [Test]
  public async Task ClaimTick_WhileAPendingPerspectiveRowIsBeingCompleted_DoesNotWaitAsync() {
    // The inbox and outbox claims already skip locked rows; the perspective claim did not, so a
    // completion holding a row it wanted to lease made the whole tick wait.
    await using var holder = await _openAsync();
    await using var claimer = await _openAsync();
    var instance = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(holder, instance);
    await _seedPendingPerspectiveRowAsync(holder, streamId, eventId, workId);

    await using var tx = await holder.BeginTransactionAsync();
    await _execAsync(holder, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_work_id = @w", ("w", workId), tx);

    await _setStatementTimeoutAsync(claimer);
    await using var cmd = claimer.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM claim_work(@i, 'test', 'test-host', 1, 10, 1, 300)";
    cmd.Parameters.AddWithValue("i", instance);
    var claimed = (long)(await cmd.ExecuteScalarAsync() ?? 0L);   // must return rather than wait

    // Skipped, not waited for -- and "skipped" is the half a statement timeout cannot show. The
    // only pending work in this database is the row the holder has locked, so a tick that returns
    // having leased nothing is the tick stepping over it; a tick that leased it would mean the
    // completion in flight and the lease disagree about who owns the row.
    await Assert.That(claimed).IsEqualTo(0);
    await tx.RollbackAsync();
  }

  // -------------------------------------------------------------------------------------------

  private static async Task _seedPendingPerspectiveRowAsync(NpgsqlConnection conn, Guid streamId, Guid eventId, Guid workId) {
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText =
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, created_at) " +
        "VALUES (@e, @s, @s, 'TestAggregate', 'TestNamespace.ProbeEvent', 'null'::jsonb, 1, nextval('wh_commit_seq'), 0, NOW() - INTERVAL '1 minute')";
      cmd.Parameters.AddWithValue("e", eventId);
      cmd.Parameters.AddWithValue("s", streamId);
      await cmd.ExecuteNonQueryAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at) VALUES (@w, @s, 'ProbePerspective', @e, 1, 0, NOW() - INTERVAL '1 minute')";
      cmd.Parameters.AddWithValue("w", workId);
      cmd.Parameters.AddWithValue("s", streamId);
      cmd.Parameters.AddWithValue("e", eventId);
      await cmd.ExecuteNonQueryAsync();
    }
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _setStatementTimeoutAsync(NpgsqlConnection conn) =>
    await _execAsync(conn, $"SET statement_timeout = {STATEMENT_TIMEOUT_MS}");

  private static async Task _execAsync(NpgsqlConnection conn, string sql, (string Name, object Value)? p = null, NpgsqlTransaction? tx = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Transaction = tx;
    if (p is { } param) {
      cmd.Parameters.AddWithValue(param.Name, param.Value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _existsAsync(NpgsqlConnection conn, string table, string keyColumn, Guid key) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM {table} WHERE {keyColumn} = @k)";
    cmd.Parameters.AddWithValue("k", key);
    return (bool)(await cmd.ExecuteScalarAsync() ?? false);
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instance) {
    await _execAsync(conn,
      "INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at) VALUES (@i, 'test', 'test-host', 1, NOW(), NOW()) ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()",
      ("i", instance));
  }

  private static async Task _storeInboxAsync(NpgsqlConnection conn, Guid messageId, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM store_inbox_messages(@p::jsonb, NULL::uuid, NULL::timestamptz, NOW(), 10000)";
    cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) {
      Value = $$"""
        [{
          "MessageId": "{{messageId}}",
          "HandlerName": "TestHandler",
          "EnvelopeType": "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
          "MessageType": "Test.X, Test",
          "Envelope": {"p":1},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": true
        }]
        """
    });
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task _storeOutboxAsync(NpgsqlConnection conn, Guid instance, Guid messageId, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM store_outbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
    cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) {
      Value = $$"""
        [{
          "MessageId": "{{messageId}}",
          "Destination": "test-topic",
          "MessageType": "TestMessage",
          "EnvelopeType": "MessageEnvelope",
          "Envelope": {},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": false
        }]
        """
    });
    cmd.Parameters.AddWithValue("inst", instance);
    _ = await cmd.ExecuteScalarAsync();
  }
}
