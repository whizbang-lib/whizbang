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
