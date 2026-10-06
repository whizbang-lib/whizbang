// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The shared LISTEN connection's remaining decisions against a real server: the forced probe on
/// the registered data source, the forced probe whose round trip never arrives, the forced probe the
/// caller abandons, the LISTEN sync that has nothing left to do, and the duplicate startup that
/// finds its liveness lock already held.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PgSharedNotifyConnectionBranchTests : EFCoreTestBase {

  // Under UseNpgsql(NpgsqlDataSource) the data source is the only credential-bearing way to reach
  // the database, so a forced probe must use it and must not need a connection string at all.
  [Test]
  [Timeout(60000)]
  public async Task ProbeNow_WithOnlyARegisteredDataSource_RoundTripsOnTheDataSourceAsync(CancellationToken cancellationToken) {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var gate = _gate(new WhizbangNotificationOptions { SelfTestTimeout = TimeSpan.FromSeconds(10) },
      new NotificationDataSource(dataSource));

    var ok = await gate.ProbeNowAsync(cancellationToken);

    await Assert.That(ok).IsTrue()
      .Because("the data source is a complete connection plan on its own; no connection string is configured");
    await Assert.That(gate.IsAvailable).IsTrue();
  }

  // The connection opens and the NOTIFY statement succeeds, but nothing is delivered (what
  // transaction pooling does to LISTEN/NOTIFY). The forced probe must report the failed round trip
  // as such, rather than as available or as an exception message.
  [Test]
  [Timeout(60000)]
  public async Task ProbeNow_WhenTheNotificationNeverArrives_RecordsTheFailedRoundTripAsync(CancellationToken cancellationToken) {
    var blackholed = await _blackholeNotifyConnectionStringAsync(cancellationToken);
    var gate = _gate(new WhizbangNotificationOptions {
      DirectConnectionString = blackholed,
      // The notification can never arrive, so the timeout is the only way out of the wait.
      SelfTestTimeout = TimeSpan.FromMilliseconds(500),
    });

    var ok = await gate.ProbeNowAsync(cancellationToken);

    await Assert.That(ok).IsFalse();
    await Assert.That(gate.IsAvailable).IsFalse();
    await Assert.That(gate.LastFailureReason).IsEqualTo("ProbeNowAsync round-trip failed");
  }

  // A caller that gives up on a forced probe (shutdown, a request timeout) is not a self-test
  // timeout, and the recorded reason must not say it was. The server here accepts the connection
  // and never answers, so the caller's cancellation is the only way the probe can end.
  [Test]
  [Timeout(60000)]
  public async Task ProbeNow_CanceledByTheCallerMidConnect_IsNotRecordedAsATimeoutAsync(CancellationToken cancellationToken) {
    using var silentServer = new TcpListener(IPAddress.Loopback, 0);
    silentServer.Start();
    var port = ((IPEndPoint)silentServer.LocalEndpoint).Port;
    var gate = _gate(new WhizbangNotificationOptions {
      DirectConnectionString = $"Host=127.0.0.1;Port={port};Username=u;Database=d;Timeout=15;Pooling=false",
    });
    using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var probe = gate.ProbeNowAsync(caller.Token);
    using var accepted = await silentServer.AcceptTcpClientAsync(cancellationToken);
    // The probe is now inside the connect handshake, waiting on a server that will never reply.
    await caller.CancelAsync();
    var ok = await probe;

    await Assert.That(ok).IsFalse();
    await Assert.That(gate.IsAvailable).IsFalse();
    await Assert.That(gate.LastFailureReason).IsNotEqualTo("ProbeNowAsync timed out")
      .Because("the caller canceled; naming it a self-test timeout would send an operator after a slow database that does not exist");
  }

  // A resync pass runs on every subscribe and every reconnect. A channel already listened, and still
  // wanted, must be left alone: re-issuing LISTEN or UNLISTEN for it is a round trip per channel per
  // pass, and on a connection that has just died each one would log a spurious failure.
  [Test]
  [Timeout(60000)]
  public async Task SyncListens_ForAChannelAlreadyListenedAndStillWanted_IssuesNothingAsync(CancellationToken cancellationToken) {
    var logger = new CapturingLogger();
    var gate = _gate(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }, logger: logger);
    var channel = $"wh_test_sync_{Guid.CreateVersion7():N}";
    using var handle = gate.Subscribe(new NoopSubscription(channel));

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await gate.SyncListensAsync(conn, cancellationToken);
    await Assert.That(gate.ListenedChannelsForTesting).Contains(channel);

    // Any statement on a closed connection throws, so a second pass that issued anything for the
    // channel would log a LISTEN or UNLISTEN failure.
    await conn.CloseAsync();
    await gate.SyncListensAsync(conn, cancellationToken);

    await Assert.That(logger.WarningCount).IsEqualTo(0)
      .Because("nothing changed for the channel, so the pass must issue nothing for it");
    await Assert.That(gate.ListenedChannelsForTesting).Contains(channel);
  }

  // A duplicate startup can leave another session holding this instance's liveness lock. The
  // connection must still come up and serve notifications, but it must not claim the lock it
  // does not hold, because the heartbeat cadence relaxes on that claim.
  [Test]
  [Timeout(60000)]
  public async Task Start_WhenAnotherSessionHoldsTheAliveLock_ComesUpWithoutClaimingItAsync(CancellationToken cancellationToken) {
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "alive-svc", "alive-host", processId: 1);
    await using var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using (var claim = new NpgsqlCommand("SELECT claim_instance_alive_lock(@id)", holder)) {
      claim.Parameters.AddWithValue("id", instance.InstanceId);
      await Assert.That(await claim.ExecuteScalarAsync(cancellationToken)).IsEqualTo(true);
    }

    var logger = new CapturingLogger();
    using var gate = new PgSharedNotifyConnection(
      Options.Create(new WhizbangNotificationOptions {
        DirectConnectionString = ConnectionString,
        SignalingMode = WorkSignalingMode.ListenNotify,
        SelfTestTimeout = TimeSpan.FromSeconds(10),
      }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      instance,
      logger);
    var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    gate.OnAvailabilityChanged += isAvailable => {
      if (isAvailable) {
        available.TrySetResult();
      }
    };

    await gate.StartAsync(cancellationToken);
    try {
      await available.Task.WaitAsync(cancellationToken);

      await Assert.That(gate.IsAvailable).IsTrue();
      await Assert.That(gate.IsAliveLockHeld).IsFalse()
        .Because("the lock belongs to the other session; claiming it would relax the heartbeat on a liveness signal nobody here holds");
      await Assert.That(logger.HasEvent(100)).IsTrue()
        .Because("the duplicate startup is reported so an operator can find the second session");
    } finally {
      await gate.StopAsync(CancellationToken.None);
    }
  }

  private static PgSharedNotifyConnection _gate(
      WhizbangNotificationOptions options,
      INotificationDataSource? dataSource = null,
      ILogger<PgSharedNotifyConnection>? logger = null) =>
    new(Options.Create(options),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      new ServiceInstanceProvider(Guid.NewGuid(), "branch-svc", "branch-host", processId: 1),
      logger,
      connectionStringFallback: null,
      timeProvider: null,
      notificationDataSource: dataSource);

  /// <summary>
  /// Shadows <c>pg_notify</c> with a no-op in a schema that precedes <c>pg_catalog</c>: the NOTIFY
  /// statement succeeds and nothing is ever delivered.
  /// </summary>
  private async Task<string> _blackholeNotifyConnectionStringAsync(CancellationToken cancellationToken) {
    await using var admin = new NpgsqlConnection(ConnectionString);
    await admin.OpenAsync(cancellationToken);
    await using var ddl = admin.CreateCommand();
    ddl.CommandText = """
      CREATE SCHEMA IF NOT EXISTS notify_blackhole;
      CREATE OR REPLACE FUNCTION notify_blackhole.pg_notify(text, text)
        RETURNS void LANGUAGE plpgsql AS $body$ BEGIN RETURN; END $body$;
      """;
    await ddl.ExecuteNonQueryAsync(cancellationToken);
    return new NpgsqlConnectionStringBuilder(ConnectionString) {
      SearchPath = "notify_blackhole,public,pg_catalog",
    }.ConnectionString;
  }

  private sealed class NoopSubscription(string channel) : INotifySubscription {
    public string ChannelName => channel;
    public void OnNotification(string payload) {
      // Delivery is not under test.
    }
  }

  private sealed class CapturingLogger : ILogger<PgSharedNotifyConnection> {
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, int EventId)> _entries = [];

    public int WarningCount {
      get {
        lock (_gate) {
          return _entries.Count(e => e.Level >= LogLevel.Warning);
        }
      }
    }

    public bool HasEvent(int eventId) {
      lock (_gate) {
        return _entries.Exists(e => e.EventId == eventId);
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_gate) {
        _entries.Add((logLevel, eventId.Id));
      }
    }
  }
}
