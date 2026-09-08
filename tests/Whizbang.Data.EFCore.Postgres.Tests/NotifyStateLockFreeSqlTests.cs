using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The doorbell debounce must never make a writer wait on another writer's transaction. Every store
/// and every handler commit toward one target instance updates that instance's row in
/// <c>wh_notify_state</c> (the watermark slide, the attempt stamp, the fire counter) INSIDE its own
/// transaction, so the row lock is held for the whole transaction. A handler-commit batch takes
/// seconds under a bulk ingest, and every store toward the same instance queued behind it on that
/// one row: on the receiving service the database showed the queue explicitly, eight
/// <c>store_inbox_messages</c> calls waiting on one <c>commit_handler_result</c> transaction that
/// was itself waiting on another, an ungranted tuple lock on <c>wh_notify_state</c> at the head, and
/// the inbox completing a dozen rows a minute with two thousand leased.
/// </summary>
/// <remarks>
/// A held state row means another writer is ringing or sliding this target right now. The right
/// move is to ring without touching the state (a spurious doorbell is absorbed by the drain's
/// refetch-until-empty loop; a lost wakeup is not) and never to wait. The suppression decision is
/// taken only when the row could be locked without waiting.
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
[Category("Shard1")]
public class NotifyStateLockFreeSqlTests : EFCoreTestBase {
  private const int STATEMENT_TIMEOUT_MS = 3000;

  [Test]
  public async Task Doorbell_WhileAnotherTransactionHoldsTheTargetsNotifyState_DoesNotWaitAndStillRingsAsync() {
    await using var holder = await _openAsync();
    await using var prober = await _openAsync();
    var target = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(holder, target);
    await _ownStreamAsync(holder, streamId, target);
    // The target's inbox doorbell state exists (a previous doorbell created it).
    await _execAsync(holder,
      "INSERT INTO wh_notify_state (instance_id, payload_kind, last_work_at, last_attempt_at, fired_count) VALUES (@i, 'inbox', NULL, NOW(), 1) ON CONFLICT DO NOTHING",
      ("i", target));

    // A handler commit in flight toward the same target: it has slid or stamped the row and holds it.
    await using var tx = await holder.BeginTransactionAsync();
    await _execAsync(holder, "SELECT 1 FROM wh_notify_state WHERE instance_id = @i AND payload_kind = 'inbox' FOR UPDATE", ("i", target), tx);

    await _setStatementTimeoutAsync(prober);
    var received = await _captureNotificationsAsync(prober, target, async () => {
      // A store toward the same target must not queue behind the commit (today: 57014).
      await using var cmd = prober.CreateCommand();
      cmd.CommandText = "SELECT notify_instance_owners('inbox', ARRAY[@sid]::uuid[])";
      cmd.Parameters.AddWithValue("sid", streamId);
      await cmd.ExecuteNonQueryAsync();
    });

    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{target}" && r.Payload == "inbox")).IsTrue()
      .Because("a held state row means someone else is ringing or sliding; ringing anyway is the safe side, waiting is the storm");

    await tx.RollbackAsync();
  }

  [Test]
  public async Task Doorbell_WhileAnotherTransactionHoldsTheTargetsNotifyState_LeavesTheStateToTheHolderAsync() {
    await using var holder = await _openAsync();
    await using var prober = await _openAsync();
    var target = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(holder, target);
    await _ownStreamAsync(holder, streamId, target);
    await _execAsync(holder,
      "INSERT INTO wh_notify_state (instance_id, payload_kind, last_work_at, last_attempt_at, fired_count) VALUES (@i, 'inbox', NULL, NOW(), 7) ON CONFLICT DO NOTHING",
      ("i", target));

    await using var tx = await holder.BeginTransactionAsync();
    await _execAsync(holder, "SELECT 1 FROM wh_notify_state WHERE instance_id = @i AND payload_kind = 'inbox' FOR UPDATE", ("i", target), tx);

    await _setStatementTimeoutAsync(prober);
    await using (var cmd = prober.CreateCommand()) {
      cmd.CommandText = "SELECT notify_instance_owners('inbox', ARRAY[@sid]::uuid[])";
      cmd.Parameters.AddWithValue("sid", streamId);
      await cmd.ExecuteNonQueryAsync();
    }
    await tx.RollbackAsync();

    await using var check = prober.CreateCommand();
    check.CommandText = "SELECT fired_count FROM wh_notify_state WHERE instance_id = @i AND payload_kind = 'inbox'";
    check.Parameters.AddWithValue("i", target);
    var fired = (long)(await check.ExecuteScalarAsync() ?? 0L);

    await Assert.That(fired).IsEqualTo(7L)
      .Because("the writer that could not take the row does not touch it: the counters are the holder's to write");
  }

  [Test]
  public async Task Doorbell_WhileAnotherTransactionIsCreatingTheTargetsNotifyState_DoesNotWaitAndStillRingsAsync() {
    // The first doorbell toward an instance creates its state row. Two first doorbells inside two
    // open transactions must not queue on that uncommitted insert: the second rings and returns.
    await using var holder = await _openAsync();
    await using var prober = await _openAsync();
    var target = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(holder, target);
    await _ownStreamAsync(holder, streamId, target);

    // The holder's doorbell creates the row and keeps its transaction open (a handler commit).
    await using var tx = await holder.BeginTransactionAsync();
    await using (var first = holder.CreateCommand()) {
      first.CommandText = "SELECT notify_instance_owners('inbox', ARRAY[@sid]::uuid[])";
      first.Transaction = tx;
      first.Parameters.AddWithValue("sid", streamId);
      await first.ExecuteNonQueryAsync();
    }

    await _setStatementTimeoutAsync(prober);
    var received = await _captureNotificationsAsync(prober, target, async () => {
      await using var cmd = prober.CreateCommand();
      cmd.CommandText = "SELECT notify_instance_owners('inbox', ARRAY[@sid]::uuid[])";
      cmd.Parameters.AddWithValue("sid", streamId);
      await cmd.ExecuteNonQueryAsync();
    });

    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{target}" && r.Payload == "inbox")).IsTrue()
      .Because("the loser of the creation race rings instead of queueing behind the winner's transaction");

    await tx.RollbackAsync();
  }

  [Test]
  public async Task Doorbell_WhenTheStateRowIsFree_StillSuppressesAFloodTowardADrainingTargetAsync() {
    // The lock-free path must not cost the debounce its purpose: with the row free, a flood toward
    // a live target that is draining is still suppressed (the 137 contract, kept green here).
    await using var conn = await _openAsync();
    var target = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, target);
    await _ownStreamAsync(conn, streamId, target);
    await _execAsync(conn,
      "INSERT INTO wh_notify_state (instance_id, payload_kind, last_work_at, last_attempt_at, rapid_run) VALUES (@i, 'inbox', NOW() - INTERVAL '2 seconds', NOW() - INTERVAL '30 milliseconds', 4) ON CONFLICT (instance_id, payload_kind) DO UPDATE SET last_work_at = EXCLUDED.last_work_at, last_attempt_at = EXCLUDED.last_attempt_at, rapid_run = EXCLUDED.rapid_run",
      ("i", target));

    var received = await _captureNotificationsAsync(conn, target, async () => {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT notify_instance_owners('inbox', ARRAY[@sid]::uuid[])";
      cmd.Parameters.AddWithValue("sid", streamId);
      await cmd.ExecuteNonQueryAsync();
    });

    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{target}")).IsFalse()
      .Because("a sustained flood toward a draining target is exactly what the debounce removes");
  }

  // -------------------------------------------------------------------------------------------

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

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instance) {
    await _execAsync(conn,
      "INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at) VALUES (@i, 'test', 'test-host', 1, NOW(), NOW()) ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()",
      ("i", instance));
  }

  private static async Task _ownStreamAsync(NpgsqlConnection conn, Guid streamId, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at) VALUES (@sid, 0, @inst, NOW())";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<List<(string Channel, string Payload)>> _captureNotificationsAsync(NpgsqlConnection conn, Guid owner, Func<Task> emit) {
    var received = new List<(string Channel, string Payload)>();
    void handler(object _, NpgsqlNotificationEventArgs e) => received.Add((e.Channel, e.Payload));
    conn.Notification += handler;
    try {
      await _execAsync(conn, $"LISTEN \"wh_work_i_{owner}\"");
      await emit();
      // 146 (#720): the functions under test queue their doorbells instead of notifying inside the
      // transaction; the caller rings after the commit. This models the driver's DoorbellRinger.
      await using (var ring = conn.CreateCommand()) {
        ring.CommandText = "SELECT ring_doorbells()";
        _ = await ring.ExecuteScalarAsync();
      }
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      await _execAsync(conn, $"UNLISTEN \"wh_work_i_{owner}\"");
    }
    return received;
  }
}
