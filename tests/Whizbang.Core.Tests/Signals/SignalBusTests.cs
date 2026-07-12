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
}
