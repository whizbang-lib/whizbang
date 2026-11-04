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

    await ((IHostedService)shared).StartAsync(cts.Token);
    await bus.StartAsync(cts.Token);
    await ((IHostedService)workListener).StartAsync(cts.Token);
    using var appSub = appChannel.Subscribe("test_topic", (_, _) => Task.CompletedTask);

    // The signal: the shared connection has issued each LISTEN.
    var workChannel = $"wh_work_i_{instanceProvider.InstanceId:D}";
    await shared.WaitForChannelListenedAsync(workChannel, cts.Token);
    await shared.WaitForChannelListenedAsync(PgSignalWire.COMMITTED_CHANNEL, cts.Token);
    await shared.WaitForChannelListenedAsync("wh_app_test_topic", cts.Token);

    // Count distinct sessions on this database that are LISTENing on our 3 channels
    // (work, committed, app) by name match.
    var listeningPids = await _countListeningSessionsAsync(workChannel, PgSignalWire.COMMITTED_CHANNEL, "wh_app_test_topic");

    // EXPECTED: exactly 1 session (the shared conn) holds LISTEN on all three channels.
    await Assert.That(listeningPids).IsEqualTo(1)
      .Because("every LISTEN is the bus's, on a single per-pod direct conn; baseline was 3 (one per listener)");

    await ((IHostedService)workListener).StopAsync(CancellationToken.None);
    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Counts distinct backend PIDs that are LISTENing on ANY of the given channels in
  /// the current database. Uses <c>pg_listening_channels()</c> evaluated per session via
  /// <c>pg_stat_activity</c> backend-PID join.
  /// </summary>
  private async Task<int> _countListeningSessionsAsync(params string[] channels) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    // pg_listening_channels() is per-backend (evaluates against current session), so we
    // can't directly query "all sessions' listened channels" from one session. Instead
    // we use the inet_server_addr / pg_stat_activity backed view alternative: query
    // pg_stat_activity for sessions with `wait_event = 'ClientRead'` AND `query` matching
    // the LISTEN commands. Simpler and works: count distinct PIDs whose recent `query`
    // text is LISTEN on our channels.
    var channelList = string.Join(" OR ", channels.Select(c => $"query LIKE '%LISTEN%\"{c}\"%'"));
    var sql = $@"
      SELECT count(DISTINCT pid)::int
      FROM pg_stat_activity
      WHERE datname = current_database()
        AND state IS NOT NULL
        AND pid != pg_backend_pid()
        AND ({channelList})";

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }
}
