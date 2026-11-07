// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Notifications.AppSignals;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Application topics on the signal bus (#2046). The bus is the <see cref="IAppSignalChannel"/>; its Postgres transport
/// emits <c>pg_notify(wh_app_&lt;topic&gt;, payload)</c> and LISTENs per subscribed topic on the instance's one
/// connection. The wire is the one an older release used, so an older instance and a newer one hear each other during a
/// rolling deploy: the tests listen and notify on the raw channel from a separate connection, as the older release does.
/// </summary>
/// <docs>fundamentals/work-coordinator/app-signals</docs>
[Category("Shard4")]
public class AppSignalsOnTheBusIntegrationTests : EFCoreTestBase {

  private IAppSignalChannel _newChannel(WhizbangNotificationOptions? options = null) {
    var opts = options ?? new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new Whizbang.Core.Observability.ServiceInstanceProvider(cfg);
    // Publishing opens a connection of its own, so a never-started shared connection is enough here.
    var shared = new PgSharedNotifyConnection(Options.Create(opts), cfg, instance, NullLogger<PgSharedNotifyConnection>.Instance);
    var transport = new PostgresSignalTransport(Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    return new SignalBus([transport], []);
  }

  [Test]
  public async Task Publish_EmitsPgNotify_OnDedicatedAppChannelAsync() {
    // Independent listener on a separate connection LISTENs on wh_app_<topic>; verifies
    // the channel actually emits pg_notify on the wire with the right channel + payload.
    const string topic = "myapp_topic";
    const string channel = "wh_app_" + topic;
    const string payload = "hello-from-test";

    await using var listenerConn = new NpgsqlConnection(ConnectionString);
    await listenerConn.OpenAsync();
    var receivedPayload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    listenerConn.Notification += (_, args) => {
      if (args.Channel == channel) {
        receivedPayload.TrySetResult(args.Payload);
      }
    };
    await using (var listenCmd = listenerConn.CreateCommand()) {
      listenCmd.CommandText = $"LISTEN {channel}";
      await listenCmd.ExecuteNonQueryAsync();
    }

    var pubChannel = _newChannel();
    await pubChannel.PublishAsync(topic, payload);

    // Notifications dispatch on the listener's connection only when it reads from the wire.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var pump = listenerConn.WaitAsync(cts.Token);
    _ = await Task.WhenAny(receivedPayload.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    await _stopPumpAsync(cts, pump);

    await Assert.That(receivedPayload.Task.IsCompleted).IsTrue()
      .Because("the bus must emit pg_notify on the wh_app_<topic> channel reachable from any LISTENing connection");
    await Assert.That(receivedPayload.Task.Result).IsEqualTo(payload);
  }

  [Test]
  public async Task Publish_WithNoConnectionStringResolved_NoOpsAsync() {
    // Defensive: if the channel can't resolve a connection string, PublishAsync is a no-op
    // (logged at Debug) rather than throwing. "No-op" is the part worth pinning — a half-wired
    // channel that still reached SOME connection would emit signals the operator never configured.
    const string topic = "any_topic";
    const string channel = "wh_app_" + topic;
    const string sentinel = "sentinel-from-the-configured-channel";

    await using var listenerConn = new NpgsqlConnection(ConnectionString);
    await listenerConn.OpenAsync();
    var firstReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    listenerConn.Notification += (_, args) => {
      if (args.Channel == channel) {
        firstReceived.TrySetResult(args.Payload);
      }
    };
    await using (var listenCmd = listenerConn.CreateCommand()) {
      listenCmd.CommandText = $"LISTEN {channel}";
      await listenCmd.ExecuteNonQueryAsync();
    }

    var unresolvable = _newChannel(new WhizbangNotificationOptions {
      // No DirectConnectionString, no ConnectionStringKey → resolver returns null.
    });
    await unresolvable.PublishAsync(topic, "must-never-reach-the-wire");

    // Deterministic negative: a configured channel publishes on the SAME channel afterwards, and
    // notifications arrive in commit order — so if the unresolvable publish had emitted, its
    // payload would be the one latched here.
    await _newChannel().PublishAsync(topic, sentinel);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var pump = listenerConn.WaitAsync(cts.Token);   // pump: notifications dispatch only while reading
    var received = await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
    await _stopPumpAsync(cts, pump);

    await Assert.That(received).IsEqualTo(sentinel)
      .Because("a channel with no connection string must drop the publish, not emit it through "
             + "some other connection");
  }

  [Test]
  public async Task SubscribeAndPublish_RoundTripsThroughTheBusAsync(CancellationToken cancellationToken) {
    const string topic = "myapp_topic";
    await using var stack = PgSignalBusStack.Create(ConnectionString);
    IAppSignalChannel channel = stack.Bus;
    await stack.StartAsync(cancellationToken);

    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscription = channel.Subscribe(topic, (payload, _) => {
      received.TrySetResult(payload);
      return Task.CompletedTask;
    });
    // Deterministic completion signal: Subscribe registers intent; the dispatch loop issues the
    // LISTEN asynchronously, and pg_notify never queues, so publishing before it lands loses the
    // payload for good.
    await stack.ListenedAsync($"wh_app_{topic}", cancellationToken);

    await channel.PublishAsync(topic, "should-be-delivered", cancellationToken);

    var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    await Assert.That(payload).IsEqualTo("should-be-delivered");
  }

  [Test]
  public async Task AnOlderInstancesRawNotify_ReachesABusSubscriberAsync(CancellationToken cancellationToken) {
    // Rolling deploy: an instance on an older release publishes with a bare pg_notify on the same channel.
    const string topic = "myapp_mixed_fleet";
    await using var stack = PgSignalBusStack.Create(ConnectionString);
    IAppSignalChannel channel = stack.Bus;
    await stack.StartAsync(cancellationToken);
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscription = channel.Subscribe(topic, (payload, _) => {
      received.TrySetResult(payload);
      return Task.CompletedTask;
    });
    await stack.ListenedAsync($"wh_app_{topic}", cancellationToken);

    await using (var older = new NpgsqlConnection(ConnectionString)) {
      await older.OpenAsync(cancellationToken);
      await using var notify = new NpgsqlCommand($"SELECT pg_notify('wh_app_{topic}', 'from-an-older-release')", older);
      _ = await notify.ExecuteScalarAsync(cancellationToken);
    }

    await Assert.That(await received.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)).IsEqualTo("from-an-older-release");
  }

  [Test]
  public async Task Subscribe_MultipleHandlersOnSameTopic_AllReceiveDeliveriesAsync(CancellationToken cancellationToken) {
    const string topic = "myapp_fanout";
    await using var stack = PgSignalBusStack.Create(ConnectionString);
    IAppSignalChannel channel = stack.Bus;
    await stack.StartAsync(cancellationToken);

    var a = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var b = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subA = channel.Subscribe(topic, (p, _) => { a.TrySetResult(p); return Task.CompletedTask; });
    using var subB = channel.Subscribe(topic, (p, _) => { b.TrySetResult(p); return Task.CompletedTask; });
    await stack.ListenedAsync($"wh_app_{topic}", cancellationToken);

    await channel.PublishAsync(topic, "fanout-payload", cancellationToken);

    var resA = await a.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    var resB = await b.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    await Assert.That(resA).IsEqualTo("fanout-payload");
    await Assert.That(resB).IsEqualTo("fanout-payload");
  }

  /// <summary>
  /// Ends the listener's read pump before the connection is disposed: disposing a connection while its wait is still
  /// reading fails with "the connection is already in state 'Waiting'".
  /// </summary>
  private static async Task _stopPumpAsync(CancellationTokenSource cts, Task pump) {
    await cts.CancelAsync();
    try {
      await pump;
    } catch (OperationCanceledException) {
      // the cancellation is how the pump ends
    }
  }
}
