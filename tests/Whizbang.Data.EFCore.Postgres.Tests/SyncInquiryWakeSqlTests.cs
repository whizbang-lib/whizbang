using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Audit gap #4 regression lock. <c>resolve_sync_inquiries</c> was inline in the legacy
/// <c>process_work_batch</c>'s transaction; the new architecture decouples it. After
/// slice 27 (migration 045) the wake-up NOTIFY is no longer global — it's routed per
/// owner instance via <c>pg_notify('wh_work_i_&lt;instance_id&gt;', payload)</c>, resolved
/// through <c>wh_active_streams</c>. The test wires that resolution explicitly: pin the
/// stream's owner to a known instance and LISTEN on that instance's channel.
/// </summary>
/// <docs>fundamentals/perspectives/sync</docs>
public class SyncInquiryWakeSqlTests : EFCoreTestBase {

  [Test]
  public async Task CompletePerspective_EmitsPgNotify_OnPerspectiveChannelAsync() {
    var ownerInstanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var workId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Use a separate connection to seed + fire complete_perspective.
    await using var fireConn = new NpgsqlConnection(ConnectionString);
    await fireConn.OpenAsync();

    // Pin the stream's owner in wh_active_streams — notify_instance_owners (mig 045)
    // joins on this table to resolve the per-instance channel.
    await using (var ins = fireConn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
        VALUES (@stream, @owner, NOW() + INTERVAL '5 minutes', 0, NOW())
        ON CONFLICT (stream_id) DO UPDATE SET assigned_instance_id = EXCLUDED.assigned_instance_id";
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("owner", ownerInstanceId);
      await ins.ExecuteNonQueryAsync();
    }

    // Insert a perspective_event so the work_id is real (complete_perspective does a
    // process_perspective_event_completions DELETE underneath; needs a row to act on).
    await using (var ins = fireConn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@work, @stream, 'TestPerspective', @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("work", workId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("eid", eventId);
      await ins.ExecuteNonQueryAsync();
    }

    // Open a dedicated LISTEN connection on the owner's per-instance channel.
    await using var listenConn = new NpgsqlConnection(ConnectionString);
    await listenConn.OpenAsync();
    var notifications = new List<string>();
    var firstNotification = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    listenConn.Notification += (_, args) => {
      notifications.Add(args.Payload);
      firstNotification.TrySetResult(args.Payload);
    };
    var channel = $"wh_work_i_{ownerInstanceId}";
    await using (var listen = listenConn.CreateCommand()) {
      listen.CommandText = $"LISTEN \"{channel}\"";
      await listen.ExecuteNonQueryAsync();
    }

    // complete_perspective fires the NOTIFY only when CURSORS advance — pass a cursor.
    await using (var fire = fireConn.CreateCommand()) {
      fire.CommandText = "SELECT complete_perspective(@cursors::jsonb, @ids, FALSE)";
      fire.Parameters.Add(new NpgsqlParameter("cursors", NpgsqlDbType.Jsonb) {
        Value = $"[{{\"StreamId\":\"{streamId}\",\"PerspectiveName\":\"TestPerspective\",\"LastEventId\":\"{eventId}\",\"Status\":1}}]"
      });
      fire.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { workId } });
      _ = await fire.ExecuteScalarAsync();
    }

    // Drive notification delivery on the listener side. Npgsql delivers notifications
    // only when the connection is read from. Use a cancellable WaitAsync so the call
    // returns cleanly once the notification arrives (or the timeout fires) — leaving
    // WaitAsync dangling in "Waiting" state breaks subsequent test runs with
    // NpgsqlOperationInProgressException.
    using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var waitTask = Task.Run(async () => {
      try { await listenConn.WaitAsync(waitCts.Token); } catch (OperationCanceledException) { }
    });
    await Task.WhenAny(firstNotification.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    waitCts.Cancel();
    await waitTask;

    await Assert.That(firstNotification.Task.IsCompleted).IsTrue()
      .Because($"complete_perspective must emit pg_notify('{channel}', 'perspective') so sync awaiters wake within the LISTEN tick.");
    await Assert.That(notifications).Contains("perspective");
  }
}
