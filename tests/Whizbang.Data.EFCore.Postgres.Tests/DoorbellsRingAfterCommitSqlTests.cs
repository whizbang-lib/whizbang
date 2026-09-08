using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 146: no hot-path transaction issues NOTIFY. PostgreSQL serializes the commit of every
/// transaction that called <c>pg_notify</c> on one database-wide lock (<c>pg_locks</c>: locktype
/// <c>object</c>, classid <c>pg_database</c>, objid 0), so a doorbell inside a 50-item commit batch
/// made every other notifying commit in the database wait for it. The hot path now queues its doorbell;
/// <c>ring_doorbells()</c> rings the queue in its own tiny commit.
/// </summary>
/// <remarks>
/// Measured live before this migration: handler-commit flushes of 6 to 27 seconds with
/// <c>complete_perspective</c>, <c>stamp_pending_commit_sequences</c> and the inbox fetch all waiting on
/// that one lock behind a commit batch (#720).
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
[Category("Shard1")]
public class DoorbellsRingAfterCommitSqlTests : EFCoreTestBase {

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, (string Name, object Value)? p = null, NpgsqlTransaction? tx = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Transaction = tx;
    if (p is { } param) {
      cmd.Parameters.AddWithValue(param.Name, param.Value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, (string Name, object Value)? p = null, NpgsqlTransaction? tx = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Transaction = tx;
    if (p is { } param) {
      cmd.Parameters.AddWithValue(param.Name, param.Value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instance) {
    await _execAsync(conn,
      "INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at) VALUES (@i, 'test', 'test-host', 1, NOW(), NOW()) ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()",
      ("i", instance));
  }

  private static async Task _ownStreamAsync(NpgsqlConnection conn, Guid streamId, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry) VALUES (@sid, 0, @inst, NOW(), NOW() + INTERVAL '5 minutes')";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<List<(string Channel, string Payload)>> _captureAsync(NpgsqlConnection conn, string channel, Func<Task> act) {
    var received = new List<(string Channel, string Payload)>();
    void handler(object _, NpgsqlNotificationEventArgs e) => received.Add((e.Channel, e.Payload));
    conn.Notification += handler;
    try {
      await _execAsync(conn, $"LISTEN \"{channel}\"");
      await act();
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      await _execAsync(conn, $"UNLISTEN \"{channel}\"");
    }
    return received;
  }

  /// <summary>Whether the given backend holds the NOTIFY serialization lock (taken at pre-commit by notifying transactions).</summary>
  private static async Task<bool> _holdsNotifyLockAsync(NpgsqlConnection observer, int pid) =>
    await _scalarAsync(observer,
      "SELECT count(*) FROM pg_locks WHERE pid = @pid AND locktype = 'object' AND classid = 'pg_database'::regclass AND objid = 0",
      ("pid", pid)) > 0;

  [Test]
  public async Task Doorbell_InsideAHotTransaction_QueuesInsteadOfNotifyingAsync() {
    await using var hot = await _openAsync();
    var target = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(hot, target);
    await _ownStreamAsync(hot, streamId, target);

    await using var tx = await hot.BeginTransactionAsync();
    await _execAsync(hot, "SELECT notify_instance_owners('inbox', ARRAY[@sid]::uuid[])", ("sid", streamId), tx);

    // The doorbell is a queued row visible to this transaction, and nothing has been notified yet.
    var queued = await _scalarAsync(hot, "SELECT count(*) FROM wh_doorbell_queue WHERE channel = 'wh_work_i_' || @t::text", ("t", target), tx);
    await Assert.That(queued).IsEqualTo(1L)
      .Because("the hot path records the doorbell as a row; the ring happens after its commit");

    await tx.RollbackAsync();
    var afterRollback = await _scalarAsync(hot, "SELECT count(*) FROM wh_doorbell_queue WHERE channel = 'wh_work_i_' || @t::text", ("t", target));
    await Assert.That(afterRollback).IsEqualTo(0L)
      .Because("a doorbell for work that never committed rolls back with it: no spurious ring");
  }

  [Test]
  public async Task HotStore_DoesNotTakeTheNotifySerializationLockAsync() {
    await using var hot = await _openAsync();
    await using var observer = await _openAsync();
    var target = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(hot, target);
    await _ownStreamAsync(hot, streamId, target);
    var pid = (int)await _scalarAsync(hot, "SELECT pg_backend_pid()");

    // A notifying transaction takes the lock at pre-commit; we hold the transaction open past the doorbell call
    // and check from outside. Before 146 the lock appears at commit; after 146 the transaction never notifies,
    // so the lock is never taken, which we can only observe by committing and looking at what pg_notify'd.
    // The direct proof is the queue: the transaction ends with rows queued and nothing notified.
    await using var tx = await hot.BeginTransactionAsync();
    await _execAsync(hot, "SELECT notify_instance_owners('inbox', ARRAY[@sid]::uuid[])", ("sid", streamId), tx);
    var lockedBeforeCommit = await _holdsNotifyLockAsync(observer, pid);
    await Assert.That(lockedBeforeCommit).IsFalse()
      .Because("the lock is taken at pre-commit, so nothing is expected here in either version; this pins the observer's method");
    await tx.CommitAsync();

    // After commit: the doorbell is still only queued (the driver rings separately).
    var pending = await _scalarAsync(hot, "SELECT count(*) FROM wh_doorbell_queue WHERE channel = 'wh_work_i_' || @t::text", ("t", target));
    await Assert.That(pending).IsEqualTo(1L)
      .Because("the committing transaction issued no NOTIFY, so it never took the serialization lock; the ring is a separate commit");
  }

  [Test]
  public async Task RingDoorbells_DeliversQueuedRingsAndCoalescesDuplicatesAsync() {
    await using var ringer = await _openAsync();
    await using var listener = await _openAsync();
    var target = (Guid)TrackedGuid.NewMedo();
    var channel = $"wh_work_i_{target}";

    // Three identical doorbells and one different payload queued by earlier (committed) hot transactions.
    for (var i = 0; i < 3; i++) {
      await _execAsync(ringer, "SELECT _queue_doorbell(@c, 'inbox')", ("c", channel));
    }
    await _execAsync(ringer, "SELECT _queue_doorbell(@c, 'perspective')", ("c", channel));

    var received = await _captureAsync(listener, channel, async () => {
      var rung = await _scalarAsync(ringer, "SELECT ring_doorbells(1000)");
      await Assert.That(rung).IsEqualTo(2L).Because("identical (channel, payload) pairs coalesce into one ring");
    });

    await Assert.That(received.Count(r => r.Payload == "inbox")).IsEqualTo(1)
      .Because("three queued inbox doorbells for one target are one wake");
    await Assert.That(received.Count(r => r.Payload == "perspective")).IsEqualTo(1);
    var left = await _scalarAsync(ringer, "SELECT count(*) FROM wh_doorbell_queue WHERE channel = @c", ("c", channel));
    await Assert.That(left).IsEqualTo(0L).Because("rung doorbells leave the queue");
  }

  [Test]
  public async Task RingDoorbells_TwoRingersDoNotDoubleRingOrBlockEachOtherAsync() {
    await using var a = await _openAsync();
    await using var b = await _openAsync();
    await using var listener = await _openAsync();
    var target = (Guid)TrackedGuid.NewMedo();
    var channel = $"wh_work_i_{target}";
    await _execAsync(a, "SELECT _queue_doorbell(@c, 'inbox')", ("c", channel));

    // Ringer A takes the row inside an open transaction (FOR UPDATE SKIP LOCKED); ringer B must neither wait nor ring it.
    await using var txA = await a.BeginTransactionAsync();
    var rungA = await _scalarAsync(a, "SELECT ring_doorbells(1000)", tx: txA);
    await _execAsync(b, "SET statement_timeout = 2000");
    var rungB = await _scalarAsync(b, "SELECT ring_doorbells(1000)");
    await Assert.That(rungA).IsEqualTo(1L);
    await Assert.That(rungB).IsEqualTo(0L).Because("a row another ringer holds is skipped, never waited on");

    var received = await _captureAsync(listener, channel, async () => await txA.CommitAsync());
    await Assert.That(received.Count).IsEqualTo(1).Because("exactly one ring reaches the listener, on the holder's commit");
  }
}
