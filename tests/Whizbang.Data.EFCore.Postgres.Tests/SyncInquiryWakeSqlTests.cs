using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Audit gap #4 regression lock. <c>resolve_sync_inquiries</c> was inline in
/// <c>process_work_batch</c>'s transaction; the new architecture decouples it. The audit
/// recommended verifying that <c>complete_perspective</c> still emits
/// <c>pg_notify('wh_work', 'perspective')</c> so sync awaiters wake within the LISTEN tick
/// rather than waiting for the polling-fallback safety-net.
/// </summary>
/// <docs>fundamentals/perspectives/sync</docs>
public class SyncInquiryWakeSqlTests : EFCoreTestBase {

  [Test]
  public async Task CompletePerspective_EmitsPgNotify_OnPerspectiveChannelAsync() {
    // Open a dedicated LISTEN connection — pg_notify is delivered to all sessions LISTENing
    // on the channel within their next round-trip.
    await using var listenConn = new NpgsqlConnection(ConnectionString);
    await listenConn.OpenAsync();
    var notifications = new List<string>();
    var firstNotification = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    listenConn.Notification += (_, args) => {
      notifications.Add(args.Payload);
      firstNotification.TrySetResult(args.Payload);
    };
    await using (var listen = listenConn.CreateCommand()) {
      listen.CommandText = "LISTEN wh_work";
      await listen.ExecuteNonQueryAsync();
    }

    // Use a separate connection to fire complete_perspective.
    await using var fireConn = new NpgsqlConnection(ConnectionString);
    await fireConn.OpenAsync();

    var streamId = Guid.NewGuid();
    var workId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

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

    await using (var fire = fireConn.CreateCommand()) {
      fire.CommandText = "SELECT complete_perspective('[]'::jsonb, @ids, FALSE)";
      fire.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { workId } });
      _ = await fire.ExecuteScalarAsync();
    }

    // Trigger notification delivery on the listener side. Npgsql delivers notifications
    // when the connection is read from. Use a cancellable WaitAsync so the call returns
    // cleanly once the notification arrives (or the timeout fires) — leaving WaitAsync
    // dangling in "Waiting" state breaks subsequent test runs with
    // NpgsqlOperationInProgressException.
    using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var waitTask = Task.Run(async () => {
      try { await listenConn.WaitAsync(waitCts.Token); } catch (OperationCanceledException) { }
    });
    await Task.WhenAny(firstNotification.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    waitCts.Cancel();
    await waitTask;

    await Assert.That(firstNotification.Task.IsCompleted).IsTrue()
      .Because("complete_perspective must emit pg_notify('wh_work', 'perspective') so sync awaiters wake within the LISTEN tick.");
    await Assert.That(notifications).Contains("perspective");
  }
}
