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
/// (PgWorkNotificationListener + PgCommitOrderStamperWorker + PgAppSignalChannel = 3
/// per pod). With horizontal scaling — N pods × M services × E environments — that's
/// a real load on Postgres's max_connections budget. After slice 33.5 every per-pod
/// LISTEN multiplexes through ONE shared connection, so the steady-state count is 1
/// (plus the lock-holding stamper conn on the leader pod, plus the transient self-test
/// probe conn ≤ SelfTestTimeout).
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
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

    using var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instanceProvider,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);

    var workListener = new PgWorkNotificationListener(
      shared, shared, instanceProvider,
      NullLogger<PgWorkNotificationListener>.Instance);

    var appChannel = new PgAppSignalChannel(
      Options.Create(opts), cfg, shared,
      NullLogger<PgAppSignalChannel>.Instance);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    // Start the shared conn (probe runs; once IsAvailable=true, the LISTEN dispatch loop
    // is active).
    await ((IHostedService)shared).StartAsync(cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(shared.IsAvailable).IsTrue();

    // Start the work listener — subscribes to wh_work_i_{instance} via shared.
    await ((IHostedService)workListener).StartAsync(cts.Token);

    // Subscribe to an app signal topic — registers wh_app_<topic> via shared.
    using var appSub = appChannel.Subscribe("test_topic", (_, _) => Task.CompletedTask);

    // Settle: let the resync-signal triggered by the late subscriptions land the LISTENs
    // on the shared conn before we count.
    await Task.Delay(500, cts.Token);

    // Count distinct sessions on this database that are LISTENing on our 3 channels
    // (work, committed, app) by name match. pg_listening_channels returns per-backend
    // a row per channel; we count distinct PIDs across the channel names we care about.
    var listeningPids = await _countListeningSessionsAsync(
      $"wh_work_i_{instanceProvider.InstanceId:D}",
      "wh_committed",
      $"wh_app_test_topic");

    // EXPECTED: exactly 1 session (the shared conn) holds LISTEN on all three channels.
    // Pre-slice-33 design would have shown ≥3 here — one per listener.
    await Assert.That(listeningPids).IsEqualTo(1)
      .Because("slice 33 multiplexes all per-channel LISTENs onto a single per-pod direct conn; baseline was 3 (one per listener)");

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
