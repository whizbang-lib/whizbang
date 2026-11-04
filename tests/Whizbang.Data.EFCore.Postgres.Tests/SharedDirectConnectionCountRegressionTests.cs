// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 33.5 connection-count regression — the headline win of the slice 33 surgery.
/// Pre-slice-33 each pod opened ONE direct connection per per-channel listener
/// (the work listener, the commit-order stamper and the app-signal channel = 3
/// per pod). With horizontal scaling — N pods × M services × E environments — that's
/// a real load on Postgres's max_connections budget. After slice 33.5 every per-pod
/// LISTEN multiplexes through ONE shared connection, so the steady-state count is 1
/// (plus the lock-holding stamper conn on the leader pod, plus the transient self-test
/// probe conn ≤ SelfTestTimeout).
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
[Category("Shard4")]
public class SharedDirectConnectionCountRegressionTests : EFCoreTestBase {

  /// <summary>
  /// Wires up the full stack — shared conn + work listener + stamper (if leader) +
  /// app-signal subscriber — and asserts the per-pod long-lived direct connection
  /// count via <c>pg_stat_activity</c>. Counts only sessions LISTENing on
  /// channels we registered, to avoid coupling to the DbContext's own pooled conns.
  /// </summary>
  [Test]
  public async Task SharedConn_OneListenerSessionPerPod_AcrossAllRegisteredSubscribersAsync() {
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
      SelfTestTimeout = TimeSpan.FromSeconds(5),
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instanceProvider = new Whizbang.Core.Observability.ServiceInstanceProvider(cfg);

    // #2046: every subscriber listens through the signal bus, whose transport is the one shared connection.
    using var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instanceProvider,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    using var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instanceProvider, NullLogger<PostgresSignalTransport>.Instance);
    var bus = new Whizbang.Core.Signals.SignalBus([transport], []);
    var workListener = new PgWorkNotificationListener(bus, shared, NullLogger<PgWorkNotificationListener>.Instance);
    Whizbang.Core.Notifications.AppSignals.IAppSignalChannel appChannel = bus;

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    shared.OnAvailabilityChanged += isAvailable => {
      if (isAvailable) {
        available.TrySetResult();
      }
    };

    await ((IHostedService)shared).StartAsync(cts.Token);
    await bus.StartAsync(cts.Token);
    await ((IHostedService)workListener).StartAsync(cts.Token);
    using var appSub = appChannel.Subscribe("test_topic", (_, _) => Task.CompletedTask);

    // The signals: the functionality probe has finished (its ephemeral connection is closed), and the shared
    // connection has issued every LISTEN: the instance channel, the broadcast channel, each coordination channel and
    // the application topic.
    if (!shared.IsAvailable) {
      await available.Task.WaitAsync(cts.Token);
    }
    string[] channels = [
      $"wh_work_i_{instanceProvider.InstanceId:D}", "wh_signal_broadcast", "wh_app_test_topic",
      .. PgSignalWire.Bindings.Select(b => b.Channel),
    ];
    foreach (var channel in channels) {
      await shared.WaitForChannelListenedAsync(channel, cts.Token);
    }

    // EXPECTED: exactly 1 session of this instance (the shared conn, named for the instance) holds every LISTEN.
    await Assert.That(await _countInstanceSessionsAsync(instanceProvider.InstanceId)).IsEqualTo(1)
      .Because("every LISTEN is the bus's, on a single per-pod direct conn; baseline was 3 (one per listener)");

    await ((IHostedService)workListener).StopAsync(CancellationToken.None);
    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  /// <summary>Counts the sessions open under the instance's application name.</summary>
  private async Task<int> _countInstanceSessionsAsync(Guid instanceId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(DISTINCT pid)::int
      FROM pg_stat_activity
      WHERE datname = current_database()
        AND application_name = @app
        AND pid != pg_backend_pid()";
    cmd.Parameters.AddWithValue("app", $"whizbang-{instanceId:D}");
    return (int)(await cmd.ExecuteScalarAsync())!;
  }
}
