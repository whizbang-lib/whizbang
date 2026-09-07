using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// Slice 33.2 — end-to-end probe round-trip against real Postgres: the gate LISTENs a
/// self-test channel, emits pg_notify via a second connection, observes the round-trip
/// within SelfTestTimeout, and flips IsAvailable=true. Mirrors the existing
/// <c>PgWorkNotificationListenerIntegrationTests</c> setup (uses the shared test container
/// per-test database).
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
[Category("Shard1")]
public class PgSharedNotifyConnectionProbeIntegrationTests : EFCoreTestBase {
  private static readonly bool[] _expectedSingleTrueTransition = [true];

  private PgSharedNotifyConnection _newGate(WhizbangNotificationOptions options) {
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgSharedNotifyConnection(
      Options.Create(options),
      config,
      new Whizbang.Core.Observability.ServiceInstanceProvider(config),
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
  }

  [Test]
  public async Task ProbeNowAsync_AgainstRealPostgres_RoundTripsAndSetsAvailableTrueAsync() {
    var gate = _newGate(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.Auto,
      SelfTestTimeout = TimeSpan.FromSeconds(5),  // generous for CI
    });

    var transitions = new List<bool>();
    gate.OnAvailabilityChanged += b => transitions.Add(b);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var ok = await gate.ProbeNowAsync(cts.Token);

    await Assert.That(ok).IsTrue();
    await Assert.That(gate.IsAvailable).IsTrue();
    await Assert.That(gate.LastVerifiedAt).IsNotNull();
    await Assert.That(transitions).IsEquivalentTo(_expectedSingleTrueTransition);
  }

  [Test]
  public async Task BackgroundService_OnStart_RunsProbe_AndBecomesAvailableAsync() {
    var gate = _newGate(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
      SelfTestTimeout = TimeSpan.FromSeconds(5),
    });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await gate.StartAsync(cts.Token);

    // Wait for the probe to land. Existing PgWorkNotificationListenerIntegrationTests use
    // a 15 s poll loop with 50 ms tick — same pattern here since the probe runs once on
    // startup and IsAvailable flips synchronously inside ExecuteAsync.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!gate.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }

    await Assert.That(gate.IsAvailable).IsTrue();
    await Assert.That(gate.LastVerifiedAt).IsNotNull();

    await gate.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ProbeNowAsync_VeryShortTimeout_AgainstReachableServerStillSucceedsAsync() {
    // 100ms is tight but should be plenty for a local container round-trip. Locks the
    // "fast path" expectation — probes shouldn't routinely take seconds.
    var gate = _newGate(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.Auto,
      SelfTestTimeout = TimeSpan.FromMilliseconds(2000),
    });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var ok = await gate.ProbeNowAsync(cts.Token);

    await Assert.That(ok).IsTrue();
  }

  // ----- the probe's whole reason to exist: NOTIFY succeeds but nothing is delivered -----

  /// <summary>
  /// Shadows <c>pg_notify</c> with a no-op in a schema that precedes <c>pg_catalog</c> on the
  /// search path. The statement the probe issues then succeeds while nothing is ever delivered —
  /// which is exactly what pgbouncer in transaction-pooling mode does to a LISTEN/NOTIFY pair,
  /// and the reason <c>IsAvailable</c> is gated on a round trip rather than on "the connection
  /// opened". Returns a connection string pointed at the shadowed search path.
  /// </summary>
  private async Task<string> _blackholeNotifyConnectionStringAsync() {
    await using var admin = new NpgsqlConnection(ConnectionString);
    await admin.OpenAsync();
    await using var ddl = admin.CreateCommand();
    ddl.CommandText = """
      CREATE SCHEMA IF NOT EXISTS notify_blackhole;
      CREATE OR REPLACE FUNCTION notify_blackhole.pg_notify(text, text)
        RETURNS void LANGUAGE plpgsql AS $body$ BEGIN RETURN; END $body$;
      """;
    await ddl.ExecuteNonQueryAsync();
    return new NpgsqlConnectionStringBuilder(ConnectionString) {
      SearchPath = "notify_blackhole,public,pg_catalog",
    }.ConnectionString;
  }

  /// <summary>
  /// The connection opens, LISTEN succeeds and the NOTIFY statement returns without error — only
  /// the delivery never happens. The gate must NOT publish availability on that connection:
  /// downstream consumers (ClaimWorker's wake, the per-listener subscribers) switch off polling
  /// when <c>IsAvailable</c> is true, so announcing a connection whose notifications silently
  /// vanish stalls every signalled path in the process until something else times out.
  /// <see cref="BackgroundService_OnStart_RunsProbe_AndBecomesAvailableAsync"/> is the control:
  /// the identical setup against a database where delivery works does go available.
  /// </summary>
  [Test]
  public async Task BackgroundService_NotifyNeverDelivered_RecordsProbeFailureAndStaysUnavailableAsync() {
    var blackholed = await _blackholeNotifyConnectionStringAsync();

    var gate = _newGate(new WhizbangNotificationOptions {
      DirectConnectionString = blackholed,
      SignalingMode = WorkSignalingMode.ListenNotify,
      // Real, short, and unraceable: the notification can never arrive, so the timeout is the
      // only way out of the probe's wait loop no matter how fast the database is.
      SelfTestTimeout = TimeSpan.FromMilliseconds(500),
    });

    var availabilityTransitions = new List<bool>();
    gate.OnAvailabilityChanged += b => {
      lock (availabilityTransitions) {
        availabilityTransitions.Add(b);
      }
    };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await gate.StartAsync(cts.Token);
    try {
      // Wait on a signal the gate itself emits — the recorded failure reason — not on StartAsync,
      // which returns before ExecuteAsync has run a single line.
      var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
      while (gate.LastFailureReason is null && DateTimeOffset.UtcNow < deadline) {
        await Task.Delay(50, cts.Token);
      }

      await Assert.That(gate.LastFailureReason).IsNotNull()
        .Because("a probe that cannot round-trip has to say so; silence would leave operators with a connection that looks fine and delivers nothing");
      await Assert.That(gate.LastFailureReason!).Contains("probe")
        .Because("the recorded reason must name the self-test probe as the thing that failed — an Npgsql connection error here would mean the fixture never reached the probe at all, and the test would be proving nothing");
      await Assert.That(gate.IsAvailable).IsFalse()
        .Because("a connection whose pg_notify round-trip does not arrive must never be published as available — consumers stop polling when it is");
      await Assert.That(gate.LastVerifiedAt).IsNull()
        .Because("nothing was ever verified on this connection, so the last-verified stamp must stay unset");

      bool[] observedTransitions;
      lock (availabilityTransitions) {
        observedTransitions = [.. availabilityTransitions];
      }
      await Assert.That(observedTransitions).DoesNotContain(true)
        .Because("OnAvailabilityChanged(true) is the switch consumers use to disable polling; it must not fire for a connection that failed its probe");
    } finally {
      await gate.StopAsync(CancellationToken.None);
    }
  }

  // ----- slice 33.3 — real-Postgres end-to-end notification delivery -----

  private sealed class RecordingSubscription(string channel) : INotifySubscription {
    public string ChannelName { get; } = channel;
    public TaskCompletionSource<string> Tcs { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void OnNotification(string payload) => Tcs.TrySetResult(payload);
  }

  [Test]
  public async Task Dispatch_NotificationViaPgNotify_DeliversToSubscriberAsync() {
    var gate = _newGate(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
      SelfTestTimeout = TimeSpan.FromSeconds(5),
    });

    // Subscribe BEFORE the gate starts — the BackgroundService will issue LISTEN for our
    // channel as part of the initial connect.
    var sub = new RecordingSubscription("wh_slice33_3_test");
    using var subHandle = gate.Subscribe(sub);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await gate.StartAsync(cts.Token);

    // Wait for the gate's startup probe to land.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!gate.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(gate.IsAvailable).IsTrue();

    // Emit a notification on the subscriber's channel from a separate connection.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT pg_notify('wh_slice33_3_test', 'hello-from-test')";
      _ = await cmd.ExecuteScalarAsync();
    }

    // Subscriber must receive the payload within a generous window.
    var payload = await sub.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(payload).IsEqualTo("hello-from-test");

    await gate.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Dispatch_SubscribeAfterStart_StillReceivesNotificationsAsync() {
    var gate = _newGate(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
      SelfTestTimeout = TimeSpan.FromSeconds(5),
    });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await gate.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!gate.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(gate.IsAvailable).IsTrue();

    // Subscribe AFTER the gate is up. The Subscribe call issues LISTEN against the live
    // shared conn (slice 33.1's _issueListenIfConnectedAsync path).
    var sub = new RecordingSubscription("wh_slice33_3_after_start");
    using var subHandle = gate.Subscribe(sub);

    // Tiny settle delay to let the async LISTEN complete before we NOTIFY. Without this
    // we'd race the LISTEN issuance against the pg_notify on a separate conn.
    await Task.Delay(200, cts.Token);

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT pg_notify('wh_slice33_3_after_start', 'late-subscribe')";
      _ = await cmd.ExecuteScalarAsync();
    }

    var payload = await sub.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(payload).IsEqualTo("late-subscribe");

    await gate.StopAsync(CancellationToken.None);
  }
}
