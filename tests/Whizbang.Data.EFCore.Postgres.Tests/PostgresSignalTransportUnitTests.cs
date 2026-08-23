using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests for <see cref="PostgresSignalTransport"/> that do NOT require Postgres — they stub
/// <see cref="ISharedNotifyConnection"/> and exercise error branches (unregistered signal, no
/// connection string, unknown wire-name, dispatch-throws) that the integration round-trip doesn't
/// cover.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[Category("Shard2")]
public class PostgresSignalTransportUnitTests {
  private readonly record struct UnitBroadcastSignal(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private readonly record struct UnitTargetedSignal(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Targeted;
  }

  private readonly record struct UnitUnregisteredSignal(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private sealed class StubSharedNotifyConnection : ISharedNotifyConnection {
    public List<INotifySubscription> All { get; } = [];
    public INotifySubscription? Last => All.Count == 0 ? null : All[^1];
    public int SubscribeCount => All.Count;

    public IDisposable Subscribe(INotifySubscription subscription) {
      All.Add(subscription);
      return new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable {
      public void Dispose() { }
    }
  }

  private sealed class CountingSink : ISignalSink {
    public int Received { get; private set; }
    public bool ThrowOnReceive { get; set; }
    public bool ThrowAsyncOnReceive { get; set; }

    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Received++;
      if (ThrowOnReceive) {
        throw new InvalidOperationException("boom-sync");
      }
      if (ThrowAsyncOnReceive) {
        return _throwAsync();
      }
      return ValueTask.CompletedTask;
    }

    private static async ValueTask _throwAsync() {
      await Task.Yield();
      throw new InvalidOperationException("boom-async");
    }
  }

  private static (PostgresSignalTransport Transport, StubSharedNotifyConnection Shared, Guid InstanceId) _createTransport(
    string? connectionString = null,
    string? configConnectionKey = null,
    string? configConnectionValue = null,
    Guid? instanceId = null) {
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = connectionString,
    };
    var cfgBuilder = new ConfigurationBuilder();
    if (configConnectionKey is not null && configConnectionValue is not null) {
      cfgBuilder.AddInMemoryCollection([
        new KeyValuePair<string, string?>($"ConnectionStrings:{configConnectionKey}", configConnectionValue),
      ]);
    }
    var cfg = cfgBuilder.Build();
    var shared = new StubSharedNotifyConnection();
    var effectiveInstanceId = instanceId ?? Guid.NewGuid();
    var instanceProvider = new ServiceInstanceProvider(effectiveInstanceId, "utest-service", "utest-host", processId: 1);
    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instanceProvider,
      NullLogger<PostgresSignalTransport>.Instance);
    return (transport, shared, effectiveInstanceId);
  }

  [Test]
  public async Task StartAsync_NullSink_ThrowsAsync() {
    var (transport, _, _) = _createTransport();
    await Assert.That(() => transport.StartAsync(null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task StartAsync_SubscribesToBroadcastChannelOnSharedConnectionAsync() {
    var (transport, shared, _) = _createTransport();
    var sink = new CountingSink();

    await transport.StartAsync(sink);

    // At minimum the broadcast subscription is registered — instance-owned channel is asserted
    // by StartAsync_SubscribesToBothBroadcastAndInstanceOwnedChannelsAsync.
    await Assert.That(shared.All.Any(s => s.ChannelName == "wh_signal_broadcast")).IsTrue();
  }

  [Test]
  public async Task Broadcast_UnknownWireName_IsSilentlyIgnoredAsync() {
    var (transport, shared, _) = _createTransport();
    var sink = new CountingSink();
    await transport.StartAsync(sink);

    // Simulate a notify with a wire-name that isn't in the SignalTypeRegistry — must NOT throw
    // and must NOT deliver anything to the sink.
    shared.All.Single(s => s.ChannelName == "wh_signal_broadcast").OnNotification("utest-nonexistent-wire-name-xyz-9871");

    await Assert.That(sink.Received).IsEqualTo(0);
  }

  [Test]
  public async Task Broadcast_KnownWireName_DispatchesToSinkAsync() {
    const string wireName = "utest-broadcast-known-42871";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(UnitBroadcastSignal), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<UnitBroadcastSignal>(default, ct)),
    ]));

    var (transport, shared, _) = _createTransport();
    var sink = new CountingSink();
    await transport.StartAsync(sink);

    shared.All.Single(s => s.ChannelName == "wh_signal_broadcast").OnNotification(wireName);

    await Assert.That(sink.Received).IsEqualTo(1);
  }

  [Test]
  public async Task Broadcast_DispatchThrowsSync_IsSwallowedAsync() {
    const string wireName = "utest-broadcast-sync-throw-13521";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(UnitBroadcastSignal), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<UnitBroadcastSignal>(default, ct)),
    ]));

    var (transport, shared, _) = _createTransport();
    var sink = new CountingSink { ThrowOnReceive = true };
    await transport.StartAsync(sink);

    // The dispatch throws synchronously — the receive-loop callback must swallow the exception
    // and continue, because one bad signal must not poison the shared connection's WaitAsync loop.
    shared.All.Single(s => s.ChannelName == "wh_signal_broadcast").OnNotification(wireName);

    await Assert.That(sink.Received).IsEqualTo(1);
  }

  [Test]
  public async Task Broadcast_DispatchThrowsAsync_IsObservedOffLoopAsync() {
    const string wireName = "utest-broadcast-async-throw-98221";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(UnitBroadcastSignal), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<UnitBroadcastSignal>(default, ct)),
    ]));

    var (transport, shared, _) = _createTransport();
    var sink = new CountingSink { ThrowAsyncOnReceive = true };
    await transport.StartAsync(sink);

    shared.All.Single(s => s.ChannelName == "wh_signal_broadcast").OnNotification(wireName);

    // Give the observe continuation a chance to complete; the assertion is that Received
    // incremented and the enclosing test process did not crash.
    await Task.Yield();
    await Assert.That(sink.Received).IsEqualTo(1);
  }

  [Test]
  public async Task StartAsync_SubscribesToBothBroadcastAndInstanceOwnedChannelsAsync() {
    // Targeted receive: the transport must LISTEN on wh_signal_broadcast AND its instance's
    // per-owner channel so it picks up both broadcast signals and targeted signals routed to it
    // by notify_instance_owners on the producer side.
    var (transport, shared, instanceId) = _createTransport();

    await transport.StartAsync(new CountingSink());

    await Assert.That(shared.SubscribeCount).IsEqualTo(2);
    await Assert.That(shared.All.Any(s => s.ChannelName == "wh_signal_broadcast")).IsTrue();
    await Assert.That(shared.All.Any(s => s.ChannelName == $"wh_work_i_{instanceId:D}")).IsTrue();
  }

  [Test]
  public async Task InstanceChannel_KnownWireName_DispatchesToSinkAsync() {
    const string wireName = "utest-targeted-known-77213";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(UnitTargetedSignal), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Targeted,
        static (sink, ct) => sink.ReceiveAsync<UnitTargetedSignal>(default, ct)),
    ]));

    var (transport, shared, instanceId) = _createTransport();
    var sink = new CountingSink();
    await transport.StartAsync(sink);

    var instanceSub = shared.All.Single(s => s.ChannelName == $"wh_work_i_{instanceId:D}");
    instanceSub.OnNotification(wireName);

    await Assert.That(sink.Received).IsEqualTo(1);
  }

  [Test]
  public async Task InstanceChannel_UnknownWireName_IsSilentlyIgnoredAsync() {
    var (transport, shared, instanceId) = _createTransport();
    await transport.StartAsync(new CountingSink());

    var instanceSub = shared.All.Single(s => s.ChannelName == $"wh_work_i_{instanceId:D}");
    // Unknown wire-name on the instance channel must NOT throw and must NOT deliver.
    instanceSub.OnNotification("utest-targeted-unknown-11223");
  }

  [Test]
  public async Task PublishAsync_TargetedSignal_InstanceTarget_NoConnectionString_NoThrowAsync() {
    // Register the targeted signal so PublishAsync exercises the Instance-target routing branch
    // rather than falling back to the unregistered-signal short-circuit.
    const string wireName = "utest-targeted-instance-no-conn-91101";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(UnitTargetedSignal), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Targeted,
        static (sink, ct) => sink.ReceiveAsync<UnitTargetedSignal>(default, ct)),
    ]));

    // No connection string configured -> transport must return silently, same as broadcast path.
    var (transport, _, _) = _createTransport();
    await transport.StartAsync(new CountingSink());

    await transport.PublishAsync(new UnitTargetedSignal(1), SignalTarget.Instance(Guid.NewGuid()));
  }

  [Test]
  public async Task PublishAsync_TargetedSignal_StreamsTarget_NoConnectionString_NoThrowAsync() {
    const string wireName = "utest-targeted-streams-no-conn-10032";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(UnitTargetedSignal), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Targeted,
        static (sink, ct) => sink.ReceiveAsync<UnitTargetedSignal>(default, ct)),
    ]));

    var (transport, _, _) = _createTransport();
    await transport.StartAsync(new CountingSink());

    await transport.PublishAsync(new UnitTargetedSignal(1), SignalTarget.Streams([Guid.NewGuid()]));
  }

  // Note: the unregistered-type gate is covered by PublishAsync_UnregisteredSignal_ReturnsWithoutThrowAsync
  // above. That gate fires before any target-kind branching, so no targeted-signal variant is needed.

  [Test]
  public async Task PublishAsync_NoConnectionString_ReturnsWithoutThrowAsync() {
    // Register the signal so this test exercises the *no-connection-string* branch specifically —
    // not the unregistered-signal-type branch that fires earlier.
    const string wireName = "utest-no-conn-string-33711";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(UnitBroadcastSignal), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<UnitBroadcastSignal>(default, ct)),
    ]));

    // Options has no direct string, config has no ConnectionStrings entry -> Resolution returns
    // null and PublishAsync must return silently (logs a Debug and moves on).
    var (transport, _, _) = _createTransport();
    await transport.StartAsync(new CountingSink());

    await transport.PublishAsync(new UnitBroadcastSignal(1), SignalTarget.Broadcast);
    // Assertion: no exception thrown.
  }

  [Test]
  public async Task PublishAsync_UnregisteredSignal_ReturnsWithoutThrowAsync() {
    // The unregistered signal type has no wire-name mapping -> PublishAsync must warn+return
    // instead of throwing.
    var (transport, _, _) = _createTransport(connectionString: "Host=fake;Database=fake;Username=fake;Password=fake");
    await transport.StartAsync(new CountingSink());

    await transport.PublishAsync(new UnitUnregisteredSignal(1), SignalTarget.Broadcast);
    // Assertion: no exception thrown; no attempt at opening the connection (unregistered path
    // exits before the OpenAsync call).
  }

  private sealed class FakeSource(IReadOnlyList<SignalTypeEntry> entries) : ISignalTypeSource {
    public IReadOnlyList<SignalTypeEntry> GetSignalTypes() => entries;
  }
}
