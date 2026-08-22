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
/// Integration tests for <see cref="PgAppSignalChannel"/>. The publish path opens a real
/// Npgsql connection and emits <c>pg_notify(wh_app_&lt;topic&gt;, payload)</c>; tests verify
/// the row appears on the wire by listening from a separate connection.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Subscribe-path is NOT covered here</strong> — see <c>SubscribePathNotWired_IsKnownGap</c>.
/// <see cref="PgAppSignalChannel.Subscribe"/> registers handlers but
/// <c>PgAppSignalChannel.Dispatch</c> (the method that fans notifications to handlers) is
/// internal and never invoked from anywhere in the codebase. There is no listener that
/// LISTENs on <c>wh_app_*</c> channels and routes payloads to <c>Dispatch</c>. Subscribers
/// register but never receive anything in the current build.
/// </para>
/// <para>
/// Closing the gap requires either extending <see cref="PgWorkNotificationListener"/> to
/// LISTEN on additional channels (per active topic), or creating a sibling listener. Tracked
/// as follow-up; not blocking the LISTEN/NOTIFY rollout because the work-signal path
/// (<c>wh_work</c>) is what slice 1–6 actually uses.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/app-signals</docs>
public class PgAppSignalChannelIntegrationTests : EFCoreTestBase {

  private PgAppSignalChannel _newChannel(WhizbangNotificationOptions? options = null) {
    var opts = options ?? new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    _ = new Whizbang.Core.Observability.ServiceInstanceProvider(cfg);
    // Slice 33.5 — Publish-only callers can pass a no-op shared connection since Publish
    // doesn't route through it. Receive-side tests below use a real PgSharedNotifyConnection.
    var noOpShared = new NoOpSharedConnection();
    return new PgAppSignalChannel(
      Options.Create(opts),
      cfg,
      noOpShared,
      NullLogger<PgAppSignalChannel>.Instance);
  }

  private sealed class NoOpSharedConnection : ISharedNotifyConnection {
    public IDisposable Subscribe(INotifySubscription subscription) => new NoOpDisposable();
    private sealed class NoOpDisposable : IDisposable { public void Dispose() { } }
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
    var waitTask = listenerConn.WaitAsync(cts.Token);
    var raced = await Task.WhenAny(receivedPayload.Task, Task.Delay(TimeSpan.FromSeconds(15)));

    await Assert.That(receivedPayload.Task.IsCompleted).IsTrue()
      .Because("PgAppSignalChannel.PublishAsync must emit pg_notify on the wh_app_<topic> channel reachable from any LISTENing connection");
    await Assert.That(receivedPayload.Task.Result).IsEqualTo(payload);
  }

  [Test]
  public async Task Publish_WithNoConnectionStringResolved_NoOpsAsync() {
    // Defensive: if the channel can't resolve a connection string, PublishAsync is a no-op
    // (logged at Debug). No exception thrown.
    var channel = _newChannel(new WhizbangNotificationOptions {
      // No DirectConnectionString, no ConnectionStringKey → resolver returns null.
    });

    // No exception should propagate.
    await channel.PublishAsync("any_topic", "any_payload");
  }

  [Test]
  public async Task SubscribeAndPublish_RoundTripsViaSharedConnectionAsync() {
    // Slice 33.5 — the gap is now closed: PgAppSignalChannel.Subscribe registers an
    // INotifySubscription against the shared connection. Publish emits pg_notify on
    // wh_app_<topic>, the shared conn's dispatch loop delivers, the in-memory handler
    // fan-out fires.
    const string topic = "myapp_topic";
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new Whizbang.Core.Observability.ServiceInstanceProvider(cfg);
    using var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instance,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    var channel = new PgAppSignalChannel(
      Options.Create(opts), cfg, shared, NullLogger<PgAppSignalChannel>.Instance);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((Microsoft.Extensions.Hosting.IHostedService)shared).StartAsync(cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(shared.IsAvailable).IsTrue();

    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscription = channel.Subscribe(topic, (payload, _) => {
      received.TrySetResult(payload);
      return Task.CompletedTask;
    });

    await _publishUntilReceivedAsync(
      () => channel.PublishAsync(topic, "should-be-delivered"), cts.Token, received.Task);

    var payload = await received.Task;
    await Assert.That(payload).IsEqualTo("should-be-delivered");

    await ((Microsoft.Extensions.Hosting.IHostedService)shared).StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Subscribe_MultipleHandlersOnSameTopic_AllReceiveDeliveriesAsync() {
    const string topic = "myapp_fanout";
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new Whizbang.Core.Observability.ServiceInstanceProvider(cfg);
    using var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instance,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    var channel = new PgAppSignalChannel(
      Options.Create(opts), cfg, shared, NullLogger<PgAppSignalChannel>.Instance);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((Microsoft.Extensions.Hosting.IHostedService)shared).StartAsync(cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }

    var a = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var b = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subA = channel.Subscribe(topic, (p, _) => { a.TrySetResult(p); return Task.CompletedTask; });
    using var subB = channel.Subscribe(topic, (p, _) => { b.TrySetResult(p); return Task.CompletedTask; });

    await _publishUntilReceivedAsync(
      () => channel.PublishAsync(topic, "fanout-payload"), cts.Token, a.Task, b.Task);

    var resA = await a.Task;
    var resB = await b.Task;
    await Assert.That(resA).IsEqualTo("fanout-payload");
    await Assert.That(resB).IsEqualTo("fanout-payload");

    await ((Microsoft.Extensions.Hosting.IHostedService)shared).StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Publishes until every awaited subscriber has been delivered to, or the deadline passes.
  /// </summary>
  /// <remarks>
  /// <c>pg_notify</c> reaches only sessions already LISTENing and is never queued, so a payload
  /// published before the subscribe-side LISTEN lands on the shared connection is lost permanently.
  /// <c>Subscribe</c> returning does not prove the LISTEN has been registered — the resync runs on
  /// the connection's own loop. A fixed <c>Task.Delay(200)</c> is a bet on that interval; a loaded
  /// runner loses it and the test then waits out its full timeout for a payload that no longer
  /// exists.
  ///
  /// <para>Re-publishing keeps the assertion honest: if the channel never delivers, every attempt is
  /// dropped and the test still fails on the deadline. Handlers use <c>TrySetResult</c>, so repeat
  /// deliveries are harmless.</para>
  /// </remarks>
  private static async Task _publishUntilReceivedAsync(
      Func<Task> publishAsync, CancellationToken cancellationToken, params Task[] awaited) {
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (true) {
      await publishAsync();
      try {
        await Task.WhenAll(awaited).WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
        return;
      } catch (TimeoutException) when (DateTimeOffset.UtcNow < deadline) {
        // A LISTEN had not landed when that notification went out — publish again.
      }
    }
  }
}
