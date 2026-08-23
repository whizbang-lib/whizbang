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
[Category("Shard4")]
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

    // Deterministic completion signal: StartAsync returns once intent is registered, but the
    // dispatch loop issues the LISTEN asynchronously. pg_notify has no queue, so anything
    // published before then is lost outright. Wait for the channel instead of guessing.
    await shared.WaitForChannelListenedAsync("wh_signal_broadcast", cts.Token);

    await bus.PublishAsync(new TransportProbe(1));

    var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
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

    // Deterministic completion signal — see the broadcast test above.
    await shared.WaitForChannelListenedAsync($"wh_work_i_{instance.InstanceId:D}", cts.Token);

    await bus.PublishAsync(new TargetedTransportProbe(1), SignalTarget.Instance(instance.InstanceId));

    var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
    await Assert.That(got.V).IsEqualTo(0)
      .Because("the wire carries only the signal's wire-name; the delivered instance is a default doorbell marker");

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

}
