using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
    // NOTE: IsHealthy is the SHARED connection's availability (PgWorkNotificationListener.IsHealthy
    // => _gate.IsAvailable), not proof that THIS listener's LISTEN has been registered. Subscribe()
    // is synchronous and the LISTEN lands on the connection's own loop, so healthy can be true with
    // the channel not yet subscribed. Waiting here narrows the window but cannot close it — callers
    // must use _notifyUntilSignalledAsync rather than a single fire-and-forget notify.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!listener.IsHealthy && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50);
    }
    return tcs;
  }

  /// <summary>
  /// Issues <c>pg_notify</c> on <paramref name="channel"/> until the listener reports it, or the
  /// deadline passes.
  /// </summary>
  /// <remarks>
  /// A Postgres NOTIFY is delivered only to sessions already LISTENing — it is not queued, and a
  /// notification sent a millisecond early is gone for good. Nothing observable from another
  /// session says whether a given channel is subscribed (<c>pg_listening_channels()</c> is
  /// session-local), so a single fire-and-forget notify is a race by construction: it passes when
  /// the LISTEN happens to land first and times out when it does not.
  ///
  /// <para>This suite does fail late-run under load, on a different test each time, and this test
  /// timed out waiting for a signal that was sent. Removing the readiness wait locally did NOT
  /// reproduce it, so the lost-notification window is an unproven cause rather than a confirmed
  /// one — the retry is hardening against a hazard the protocol genuinely has, not a verified fix
  /// for that failure.</para>
  ///
  /// <para>Re-sending does not weaken the assertion. The contract under test is "a notify on this
  /// channel reaches OnSignal"; if the listener never subscribes, every attempt is dropped and the
  /// test still fails on the deadline.</para>
  /// </remarks>
  private static async Task<WorkSignalCategory> _notifyUntilSignalledAsync(
      NpgsqlConnection conn, string channel, string payload,
      TaskCompletionSource<WorkSignalCategory> tcs, int timeoutSeconds = 15) {
    var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
    while (true) {
      await using (var cmd = conn.CreateCommand()) {
        cmd.CommandText = $"SELECT pg_notify('{channel}', '{payload}')";
        _ = await cmd.ExecuteScalarAsync();
      }
      try {
        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
      } catch (TimeoutException) when (DateTimeOffset.UtcNow < deadline) {
        // LISTEN had not landed yet — send again.
      }
    }
  }

  // Slice 27: each test resolves a unique instance_id (via a fresh ServiceInstanceProvider)
  // and exposes it so the test can also pin streams and emit on the routed channel.
  //
  // Slice 33.4 — listener no longer owns a connection. Each test gets a fresh
  // PgSharedNotifyConnection too; StartAsync wires the listener as a subscriber. The
  // shared-conn must also be started so its dispatch loop runs.
  private (PgWorkNotificationListener Listener, PgSharedNotifyConnection Shared, Guid InstanceId) _newListenerWithInstance(WhizbangNotificationOptions options) {
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instanceProvider = new Whizbang.Core.Observability.ServiceInstanceProvider(config);
    var shared = new PgSharedNotifyConnection(
      Options.Create(options),
      config,
      instanceProvider,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    var listener = new PgWorkNotificationListener(
      shared, shared, instanceProvider,
      NullLogger<PgWorkNotificationListener>.Instance);
    return (listener, shared, instanceProvider.InstanceId);
  }

  private PgWorkNotificationListener _newListener(WhizbangNotificationOptions options)
    => _newListenerWithInstance(options).Listener;

  /// <summary>
  /// Starts the shared connection, waits for its probe to succeed, then subscribes the
  /// listener. Returns a disposable that tears down in reverse order. Mirrors what the
  /// production DI host does — every test goes through this so the per-test setup matches
  /// real-world startup ordering.
  /// </summary>
  private static async Task<NotificationStack> _startStackAsync(
      PgWorkNotificationListener listener,
      PgSharedNotifyConnection shared,
      CancellationToken ct) {
    await ((IHostedService)shared).StartAsync(ct);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, ct);
    }
    await ((IHostedService)listener).StartAsync(ct);
    return new NotificationStack(listener, shared);
  }

  private sealed class NotificationStack(
      PgWorkNotificationListener listener,
      PgSharedNotifyConnection shared) : IAsyncDisposable {
    private bool _disposed;
    public async ValueTask DisposeAsync() {
      if (_disposed) {
        return;
      }
      _disposed = true;
      await ((IHostedService)listener).StopAsync(CancellationToken.None);
      await ((IHostedService)shared).StopAsync(CancellationToken.None);
    }
  }

  // ----- direct pg_notify round-trip -----

  [Test]
  public async Task PgNotify_OutboxCategory_FiresOnSignalWithOutboxAsync() {
    var (listener, shared, instanceId) = _newListenerWithInstance(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var stack = await _startStackAsync(listener, shared, cts.Token);
    var tcs = await _attachAsync(listener);

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var category = await _notifyUntilSignalledAsync(conn, $"wh_work_i_{instanceId}", "outbox", tcs);
    await Assert.That(category).IsEqualTo(WorkSignalCategory.Outbox);

    // stop handled by `await using var stack` above
  }

  [Test]
  public async Task PgNotify_InboxCategory_FiresOnSignalWithInboxAsync() {
    var (listener, shared, instanceId) = _newListenerWithInstance(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var stack = await _startStackAsync(listener, shared, cts.Token);
    var tcs = await _attachAsync(listener);

    // Deterministic: wait until the LISTEN for this channel is actually registered on the shared
    // connection before emitting. Subscribe only registers intent — the dispatch loop issues LISTEN
    // asynchronously — so a NOTIFY sent before then would fire into a not-yet-listening connection
    // and be lost (the prior flake: IsHealthy went true while LISTEN was still pending).
    await shared.WaitForChannelListenedAsync($"wh_work_i_{instanceId}", cts.Token);

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var category = await _notifyUntilSignalledAsync(conn, $"wh_work_i_{instanceId}", "inbox", tcs);
    await Assert.That(category).IsEqualTo(WorkSignalCategory.Inbox);

    // stop handled by `await using var stack` above
  }

  [Test]
  public async Task PgNotify_PerspectiveCategory_FiresOnSignalWithPerspectiveAsync() {
    var (listener, shared, instanceId) = _newListenerWithInstance(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var stack = await _startStackAsync(listener, shared, cts.Token);
    var tcs = await _attachAsync(listener);

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var category = await _notifyUntilSignalledAsync(conn, $"wh_work_i_{instanceId}", "perspective", tcs);
    await Assert.That(category).IsEqualTo(WorkSignalCategory.Perspective);

    // stop handled by `await using var stack` above
  }

  [Test]
  public async Task PgNotify_UnknownCategory_DoesNotFireOnSignalAsync() {
    // Defensive: payloads outside the known set are ignored. The listener still reads them
    // (which sets LastSignalAt) but does NOT fire OnSignal — so subscribers don't see noise.
    var (listener, shared, instanceId) = _newListenerWithInstance(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var stack = await _startStackAsync(listener, shared, cts.Token);
    var tcs = new TaskCompletionSource<WorkSignalCategory>(TaskCreationOptions.RunContinuationsAsynchronously);
    listener.OnSignal += cat => tcs.TrySetResult(cat);
    while (!listener.IsHealthy) { await Task.Delay(50); }

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = $"SELECT pg_notify('wh_work_i_{instanceId}', 'gibberish')";
      _ = await cmd.ExecuteScalarAsync();
    }

    // Race: give the notification a chance to land. If OnSignal fires within 1 s,
    // tcs completes and the test fails. Otherwise tcs stays pending and the test passes.
    var raced = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
    await Assert.That(tcs.Task.IsCompleted).IsFalse()
      .Because("payloads outside {outbox, inbox, perspective} must not surface as a WorkSignalCategory");

    // stop handled by `await using var stack` above
  }

  // ----- real SQL functions emit pg_notify (regression locks) -----

  [Test]
  public async Task CompletePerspective_RealSqlEmitsPgNotify_ListenerSeesPerspectiveAsync() {
    // Locks the cursor → awaiter wake linkage at the real SQL layer. If a future migration
    // strips the routed pg_notify from complete_perspective (mig 029), this test fails —
    // captures audit gap #4. Slice 27 retrofit: routing is via wh_work_i_<owner>, so the
    // test pins the stream to the listener's instance via wh_active_streams.
    var (listener, shared, instanceId) = _newListenerWithInstance(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var stack = await _startStackAsync(listener, shared, cts.Token);
    var tcs = await _attachAsync(listener);

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = Guid.NewGuid();
    var cursorEventId = Guid.NewGuid();

    // Register the listener instance + pin the stream so the routed NOTIFY can resolve.
    await using (var reg = conn.CreateCommand()) {
      reg.CommandText = @"INSERT INTO wh_service_instances
                            (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
                          VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
                          ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at";
      reg.Parameters.AddWithValue("id", instanceId);
      await reg.ExecuteNonQueryAsync();
    }
    await using (var pin = conn.CreateCommand()) {
      pin.CommandText = @"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
                          VALUES (@sid, 0, @inst, NOW())
                          ON CONFLICT (stream_id) DO UPDATE
                            SET assigned_instance_id = EXCLUDED.assigned_instance_id";
      pin.Parameters.AddWithValue("sid", streamId);
      pin.Parameters.AddWithValue("inst", instanceId);
      await pin.ExecuteNonQueryAsync();
    }

    // Advance a cursor for the owned stream — this is the wake-trigger.
    var cursorsJson = $$"""
      [{
        "StreamId": "{{streamId}}",
        "PerspectiveName": "TestPerspective",
        "CursorEventId": "{{cursorEventId}}",
        "Metadata": {}
      }]
      """;
    await using (var fire = conn.CreateCommand()) {
      fire.CommandText = "SELECT complete_perspective(@cursors::jsonb, NULL::uuid[], FALSE)";
      fire.Parameters.AddWithValue("cursors", cursorsJson);
      _ = await fire.ExecuteScalarAsync();
    }

    var category = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await Assert.That(category).IsEqualTo(WorkSignalCategory.Perspective);

    // stop handled by `await using var stack` above
  }

  // ----- listener health -----

  [Test]
  public async Task Listener_OnStart_BecomesHealthyAsync() {
    var (listener, shared, _) = _newListenerWithInstance(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var stack = await _startStackAsync(listener, shared, cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!listener.IsHealthy && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50);
    }
    await Assert.That(listener.IsHealthy).IsTrue();

    // stop handled by `await using var stack` above
  }

  // ----- reconnect under disconnect -----

  [Test]
  public async Task Listener_ServerTerminatesBackend_ReconnectsAndRecoversHealthAsync() {
    // Real disconnect scenario: postgres drops the listener's session (e.g., via
    // pg_terminate_backend, a service restart, or a transient network blip). The listener
    // should detect, log, back off, reconnect, and resume LISTENing — verified by health
    // toggling false → true and a fresh pg_notify still firing OnSignal.
    var (listener, shared, instanceId) = _newListenerWithInstance(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
      ListenReconnectInitialDelay = TimeSpan.FromMilliseconds(100),
      ListenReconnectMaxDelay = TimeSpan.FromMilliseconds(500),
    });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    await using var stack = await _startStackAsync(listener, shared, cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!listener.IsHealthy && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50);
    }
    await Assert.That(listener.IsHealthy).IsTrue().Because("listener must establish initial LISTEN before disconnect test");

    var healthDropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var healthRecovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    listener.OnHealthChanged += healthy => {
      if (!healthy) { healthDropped.TrySetResult(); } else if (healthDropped.Task.IsCompleted) { healthRecovered.TrySetResult(); }
    };

    // Terminate the listener's backend session. Match by query text — the LISTEN session
    // sits idle with `query` retaining 'LISTEN wh_work' in pg_stat_activity.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var kill = conn.CreateCommand()) {
      kill.CommandText = @"
        SELECT pg_terminate_backend(pid)
        FROM pg_stat_activity
        WHERE datname = current_database()
          AND query LIKE '%LISTEN%wh_work_i_%'
          AND pid != pg_backend_pid()";
      _ = await kill.ExecuteScalarAsync();
    }

    // Health goes false on detection, true again after reconnect.
    await healthDropped.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await healthRecovered.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await Assert.That(listener.IsHealthy).IsTrue().Because("after reconnect listener should be healthy again");

    // Verify the new connection is actually listening — fire pg_notify, expect OnSignal.
    var tcs = new TaskCompletionSource<WorkSignalCategory>(TaskCreationOptions.RunContinuationsAsynchronously);
    listener.OnSignal += cat => tcs.TrySetResult(cat);
    await using (var notify = conn.CreateCommand()) {
      notify.CommandText = $"SELECT pg_notify('wh_work_i_{instanceId}', 'outbox')";
      _ = await notify.ExecuteScalarAsync();
    }
    var category = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await Assert.That(category).IsEqualTo(WorkSignalCategory.Outbox)
      .Because("after reconnect, fresh pg_notify must reach OnSignal subscribers");

    // stop handled by `await using var stack` above
  }
}
