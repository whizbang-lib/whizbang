using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresSignalTransport"/> — a broadcast signal published as
/// <c>pg_notify(wh_signal_broadcast, wireName)</c> must round-trip through the shared LISTEN
/// connection back to a typed subscriber on the bus.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public class PostgresSignalTransportIntegrationTests : EFCoreTestBase {
  private readonly record struct TransportProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private readonly record struct TargetedTransportProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Targeted;
  }

  private sealed class FakeSource(IReadOnlyList<SignalTypeEntry> entries) : ISignalTypeSource {
    public IReadOnlyList<SignalTypeEntry> GetSignalTypes() => entries;
  }

  [Test]
  public async Task BroadcastSignal_RoundTripsViaSharedConnectionAsync() {
    // Register the probe signal (unique wire-name, robust to the process-wide static registry).
    const string wireName = "utest-transport-probe";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(TransportProbe), wireName, SignalDeliveryClass.BestEffort,
        SignalTargeting.Broadcast, static (sink, ct) => sink.ReceiveAsync<TransportProbe>(default, ct)),
    ]));

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

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((IHostedService)shared).StartAsync(cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(shared.IsAvailable).IsTrue();

    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    var bus = new SignalBus([transport]);

    var received = new TaskCompletionSource<TransportProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<TransportProbe>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    await bus.StartAsync(cts.Token);   // registers the broadcast LISTEN on the shared connection

    var got = await _publishUntilReceivedAsync(
      async () => await bus.PublishAsync(new TransportProbe(1)), received, cts.Token);
    await Assert.That(got.V).IsEqualTo(0)   // doorbell: default instance delivered; state comes from the DB
      .Because("the wire carries only the signal's wire-name; the delivered instance is a default doorbell marker");

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task InstanceTargetedSignal_RoundTripsToOwningInstanceAsync() {
    // Verifies the targeted receive/publish loop: publish with SignalTarget.Instance(myId) emits
    // pg_notify on wh_work_i_<myId>; the transport's instance-channel LISTEN receives it and
    // dispatches the wire-name back to the typed subscriber.
    const string wireName = "utest-targeted-transport-probe-11391";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(TargetedTransportProbe), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Targeted,
        static (sink, ct) => sink.ReceiveAsync<TargetedTransportProbe>(default, ct)),
    ]));

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

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((IHostedService)shared).StartAsync(cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(shared.IsAvailable).IsTrue();

    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    var bus = new SignalBus([transport]);

    var received = new TaskCompletionSource<TargetedTransportProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<TargetedTransportProbe>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    await bus.StartAsync(cts.Token);

    var got = await _publishUntilReceivedAsync(
      async () => await bus.PublishAsync(new TargetedTransportProbe(1), SignalTarget.Instance(instance.InstanceId)),
      received, cts.Token);
    await Assert.That(got.V).IsEqualTo(0)
      .Because("the wire carries only the signal's wire-name; the delivered instance is a default doorbell marker");

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Publishes until the subscriber reports the signal, or the deadline passes.
  /// </summary>
  /// <remarks>
  /// <para>
  /// These signals ride <c>pg_notify</c>, which reaches only sessions already LISTENing — Postgres
  /// does not queue a notification for a session that has not subscribed yet, so one published a
  /// moment early is gone for good. <c>StartAsync</c> returning does not mean the LISTEN has landed
  /// on the shared connection; it is registered on that connection's own loop.
  /// </para>
  /// <para>
  /// These tests used to bridge that with <c>await Task.Delay(200)</c> and a single publish. That is
  /// a bet on a fixed interval, and on a loaded CI runner it loses: the notification is dropped, and
  /// the test then waits out its full timeout for a signal that no longer exists. Observed as
  /// <c>InstanceTargetedSignal_RoundTripsToOwningInstanceAsync</c> timing out on a busy runner while
  /// passing locally.
  /// </para>
  /// <para>
  /// Republishing does not weaken the assertion. The contract is "a published signal reaches its
  /// subscriber"; if the transport never delivers, every attempt is dropped and the test still fails
  /// on the deadline. It removes the race rather than widening the window it races in.
  /// </para>
  /// </remarks>
  private static async Task<T> _publishUntilReceivedAsync<T>(
      Func<Task> publishAsync, TaskCompletionSource<T> received, CancellationToken cancellationToken,
      int timeoutSeconds = 15) {
    var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
    while (true) {
      await publishAsync();
      try {
        return await received.Task.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
      } catch (TimeoutException) when (DateTimeOffset.UtcNow < deadline) {
        // The LISTEN had not landed when that notification went out — send another.
      }
    }
  }
}
