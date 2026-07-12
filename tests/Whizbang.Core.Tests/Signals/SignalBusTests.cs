using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

public class SignalBusTests {
  private readonly record struct TestSignal(int Value) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private readonly record struct TestTargetedSignal(int Value) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Targeted;
  }

  private sealed class CapturingTransport : ISignalTransport {
    public List<(Type SignalType, SignalTargetKind TargetKind)> Published { get; } = [];

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Published.Add((typeof(TSignal), target.Kind));
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task ReceiveAsync_WithSubscriber_InvokesHandlerWithSignalAsync() {
    var bus = new SignalBus([]);
    TestSignal? received = null;
    using var sub = bus.Subscribe<TestSignal>(s => {
      received = s;
      return ValueTask.CompletedTask;
    });

    await ((ISignalSink)bus).ReceiveAsync(new TestSignal(42));

    await Assert.That(received).IsNotNull();
    await Assert.That(received!.Value.Value).IsEqualTo(42);
  }

  [Test]
  public async Task PublishAsync_ViaInMemoryTransport_DeliversToSubscriberAsync() {
    var bus = new SignalBus([new InMemorySignalTransport()]);
    await bus.StartAsync();
    TestSignal? received = null;
    using var sub = bus.Subscribe<TestSignal>(s => {
      received = s;
      return ValueTask.CompletedTask;
    });

    await bus.PublishAsync(new TestSignal(7));

    await Assert.That(received).IsNotNull();
    await Assert.That(received!.Value.Value).IsEqualTo(7);
  }

  [Test]
  public async Task ReceiveAsync_MultipleSubscribers_AllInvokedAsync() {
    var bus = new SignalBus([]);
    var count = 0;
    using var s1 = bus.Subscribe<TestSignal>(_ => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });
    using var s2 = bus.Subscribe<TestSignal>(_ => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });

    await ((ISignalSink)bus).ReceiveAsync(new TestSignal(1));

    await Assert.That(count).IsEqualTo(2);
  }

  [Test]
  public async Task DisposedSubscription_StopsDeliveryAsync() {
    var bus = new SignalBus([]);
    var count = 0;
    var sub = bus.Subscribe<TestSignal>(_ => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });

    await ((ISignalSink)bus).ReceiveAsync(new TestSignal(1));
    sub.Dispose();
    await ((ISignalSink)bus).ReceiveAsync(new TestSignal(2));

    await Assert.That(count).IsEqualTo(1);
  }

  [Test]
  public async Task ReceiveAsync_NoSubscribers_DoesNotThrowAsync() {
    var bus = new SignalBus([]);
    // No subscribers yet — this receive must be a no-op, not a throw.
    await ((ISignalSink)bus).ReceiveAsync(new TestSignal(1));

    // A subscriber added afterwards still receives, proving the earlier no-op did no harm.
    var delivered = false;
    using var sub = bus.Subscribe<TestSignal>(_ => { delivered = true; return ValueTask.CompletedTask; });
    await ((ISignalSink)bus).ReceiveAsync(new TestSignal(2));

    await Assert.That(delivered).IsTrue();
  }

  [Test]
  public async Task PublishAsync_DefaultTarget_FlowsBroadcastToTransportAsync() {
    var transport = new CapturingTransport();
    var bus = new SignalBus([transport]);
    await bus.StartAsync();

    await bus.PublishAsync(new TestSignal(1));

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(transport.Published[0].TargetKind).IsEqualTo(SignalTargetKind.Broadcast);
  }

  [Test]
  public async Task PublishAsync_TargetedSignal_WithBroadcastTarget_ThrowsAsync() {
    var transport = new CapturingTransport();
    var bus = new SignalBus([transport]);
    await bus.StartAsync();

    await Assert.That(async () =>
      await bus.PublishAsync(new TestTargetedSignal(1)))
      .Throws<ArgumentException>()
      .Because("a Targeted signal must carry a Streams or Instance target — Broadcast is not a valid target for it");
  }

  [Test]
  public async Task PublishAsync_BroadcastSignal_WithStreamsTarget_ThrowsAsync() {
    var transport = new CapturingTransport();
    var bus = new SignalBus([transport]);
    await bus.StartAsync();

    await Assert.That(async () =>
      await bus.PublishAsync(new TestSignal(1), SignalTarget.Streams([Guid.NewGuid()])))
      .Throws<ArgumentException>()
      .Because("a Broadcast signal is delivered to every instance — a per-stream target is a caller error");
  }

  [Test]
  public async Task PublishAsync_TargetedSignal_WithStreamsTarget_FlowsToTransportAsync() {
    var transport = new CapturingTransport();
    var bus = new SignalBus([transport]);
    await bus.StartAsync();

    await bus.PublishAsync(new TestTargetedSignal(1), SignalTarget.Streams([Guid.NewGuid()]));

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(transport.Published[0].TargetKind).IsEqualTo(SignalTargetKind.Streams);
  }

  [Test]
  public async Task PublishAsync_TargetedSignal_WithInstanceTarget_FlowsToTransportAsync() {
    var transport = new CapturingTransport();
    var bus = new SignalBus([transport]);
    await bus.StartAsync();

    await bus.PublishAsync(new TestTargetedSignal(1), SignalTarget.Instance(Guid.NewGuid()));

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(transport.Published[0].TargetKind).IsEqualTo(SignalTargetKind.Instance);
  }

  [Test]
  public async Task Ctor_NullTransports_ThrowsAsync() {
    await Assert.That(() => new SignalBus(null!)).Throws<ArgumentNullException>();
  }

  private sealed class StartCountingTransport : ISignalTransport {
    public int StartCallCount { get; private set; }
    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
      StartCallCount++;
      return Task.CompletedTask;
    }
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;
  }

  [Test]
  public async Task StartAsync_StartsEveryTransportAsync() {
    var a = new StartCountingTransport();
    var b = new StartCountingTransport();
    var bus = new SignalBus([a, b]);

    await bus.StartAsync();

    await Assert.That(a.StartCallCount).IsEqualTo(1);
    await Assert.That(b.StartCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task PublishAsync_FanoutToEveryTransportAsync() {
    var a = new CapturingTransport();
    var b = new CapturingTransport();
    var bus = new SignalBus([a, b]);
    await bus.StartAsync();

    await bus.PublishAsync(new TestSignal(1));

    await Assert.That(a.Published.Count).IsEqualTo(1);
    await Assert.That(b.Published.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Subscribe_NullHandler_ThrowsAsync() {
    var bus = new SignalBus([]);
    await Assert.That(() => bus.Subscribe<TestSignal>(null!)).Throws<ArgumentNullException>();
  }

  private sealed class StartCountingPullSource : ISignalSource {
    public int StartCallCount { get; private set; }
    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
      StartCallCount++;
      return Task.CompletedTask;
    }
  }

  [Test]
  public async Task StartAsync_StartsEveryPullSourceAsync() {
    var transport = new CapturingTransport();
    var poll = new StartCountingPullSource();
    var bus = new SignalBus([transport], [poll]);

    await bus.StartAsync();

    await Assert.That(poll.StartCallCount).IsEqualTo(1)
      .Because("the bus is responsible for starting pull sources alongside push transports");
  }

  [Test]
  public async Task StartAsync_NoPullSources_DoesNotThrowAsync() {
    // The pullSources argument is optional; callers with no polling registered pass null (or omit).
    var bus = new SignalBus([new CapturingTransport()]);

    await bus.StartAsync();
    // No throw = pass.
  }
}
