using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Round-trip integration tests for <see cref="PgWorkNotificationListener"/>: real Postgres
/// emits <c>pg_notify('wh_work', category)</c>, the listener receives it, and <c>OnSignal</c>
/// fires with the correct <see cref="WorkSignalCategory"/>.
/// </summary>
/// <remarks>
/// Uses the shared test container per-test database. The listener opens its own direct
/// connection against the test DB; we issue pg_notify on a separate connection.
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PgWorkNotificationListenerIntegrationTests : EFCoreTestBase {

  private static async Task<TaskCompletionSource<WorkSignalCategory>> _attachAsync(PgWorkNotificationListener listener) {
    var tcs = new TaskCompletionSource<WorkSignalCategory>(TaskCreationOptions.RunContinuationsAsynchronously);
    listener.OnSignal += cat => tcs.TrySetResult(cat);
    // Wait until listener reports healthy before issuing the pg_notify, otherwise the
    // notify can fire before LISTEN is registered and we'd race-fail.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!listener.IsHealthy && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50);
    }
    return tcs;
  }

  private PgWorkNotificationListener _newListener(WhizbangNotificationOptions options) {
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgWorkNotificationListener(
      Options.Create(options),
      config,
      NullLogger<PgWorkNotificationListener>.Instance);
  }

  // ----- direct pg_notify round-trip -----

  [Test]
  public async Task PgNotify_OutboxCategory_FiresOnSignalWithOutboxAsync() {
    var listener = _newListener(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await listener.StartAsync(cts.Token);
    var tcs = await _attachAsync(listener);

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT pg_notify('wh_work', 'outbox')";
      _ = await cmd.ExecuteScalarAsync();
    }

    var category = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await Assert.That(category).IsEqualTo(WorkSignalCategory.Outbox);

    await listener.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task PgNotify_InboxCategory_FiresOnSignalWithInboxAsync() {
    var listener = _newListener(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await listener.StartAsync(cts.Token);
    var tcs = await _attachAsync(listener);

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT pg_notify('wh_work', 'inbox')";
      _ = await cmd.ExecuteScalarAsync();
    }

    var category = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await Assert.That(category).IsEqualTo(WorkSignalCategory.Inbox);

    await listener.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task PgNotify_PerspectiveCategory_FiresOnSignalWithPerspectiveAsync() {
    var listener = _newListener(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await listener.StartAsync(cts.Token);
    var tcs = await _attachAsync(listener);

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT pg_notify('wh_work', 'perspective')";
      _ = await cmd.ExecuteScalarAsync();
    }

    var category = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await Assert.That(category).IsEqualTo(WorkSignalCategory.Perspective);

    await listener.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task PgNotify_UnknownCategory_DoesNotFireOnSignalAsync() {
    // Defensive: payloads outside the known set are ignored. The listener still reads them
    // (which sets LastSignalAt) but does NOT fire OnSignal — so subscribers don't see noise.
    var listener = _newListener(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await listener.StartAsync(cts.Token);
    var tcs = new TaskCompletionSource<WorkSignalCategory>(TaskCreationOptions.RunContinuationsAsynchronously);
    listener.OnSignal += cat => tcs.TrySetResult(cat);
    while (!listener.IsHealthy) { await Task.Delay(50); }

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT pg_notify('wh_work', 'gibberish')";
      _ = await cmd.ExecuteScalarAsync();
    }

    // Race: give the notification a chance to land. If OnSignal fires within 1 s,
    // tcs completes and the test fails. Otherwise tcs stays pending and the test passes.
    var raced = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
    await Assert.That(tcs.Task.IsCompleted).IsFalse()
      .Because("payloads outside {outbox, inbox, perspective} must not surface as a WorkSignalCategory");

    await listener.StopAsync(CancellationToken.None);
  }

  // ----- real SQL functions emit pg_notify (regression locks) -----

  [Test]
  public async Task CompletePerspective_RealSqlEmitsPgNotify_ListenerSeesPerspectiveAsync() {
    // Locks the cursor → awaiter wake linkage at the real SQL layer. If a future migration
    // strips the pg_notify from complete_perspective (mig 029), this test fails — exactly
    // what audit gap #4 wanted captured.
    var listener = _newListener(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await listener.StartAsync(cts.Token);
    var tcs = await _attachAsync(listener);

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var workId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"INSERT INTO wh_perspective_events
                          (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
                          VALUES (@work, @stream, 'TestPerspective', @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("work", workId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("eid", eventId);
      await ins.ExecuteNonQueryAsync();
    }
    await using (var fire = conn.CreateCommand()) {
      fire.CommandText = "SELECT complete_perspective('[]'::jsonb, @ids, FALSE)";
      fire.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { workId } });
      _ = await fire.ExecuteScalarAsync();
    }

    var category = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await Assert.That(category).IsEqualTo(WorkSignalCategory.Perspective);

    await listener.StopAsync(CancellationToken.None);
  }

  // ----- listener health -----

  [Test]
  public async Task Listener_OnStart_BecomesHealthyAsync() {
    var listener = _newListener(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await listener.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!listener.IsHealthy && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50);
    }
    await Assert.That(listener.IsHealthy).IsTrue();

    await listener.StopAsync(CancellationToken.None);
  }
}
