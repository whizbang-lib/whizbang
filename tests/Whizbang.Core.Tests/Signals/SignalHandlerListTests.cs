using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Direct coverage for <see cref="SignalHandlerList{T}"/> concurrency + disposal semantics —
/// covered indirectly via bus tests, but the double-dispose and cancellation branches need
/// explicit tests to lock the invariants.
/// </summary>
public class SignalHandlerListTests {
  private readonly record struct HL(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  [Test]
  public async Task Dispose_TwiceIsIdempotentAsync() {
    var bus = new SignalBus([]);
    var count = 0;
    var sub = bus.Subscribe<HL>(_ => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });

    await ((ISignalSink)bus).ReceiveAsync(new HL(1));
    sub.Dispose();
    sub.Dispose();   // second dispose must be a no-op, not throw and not remove twice
    await ((ISignalSink)bus).ReceiveAsync(new HL(2));

    await Assert.That(count).IsEqualTo(1);
  }

  [Test]
  public async Task InvokeAsync_CanceledBetweenHandlers_ThrowsAsync() {
    var bus = new SignalBus([]);
    using var cts = new CancellationTokenSource();
    var firstInvoked = false;
    var secondInvoked = false;

    // Cancel from inside the first handler so the second handler's cancellation check throws.
    using var s1 = bus.Subscribe<HL>(_ => {
      firstInvoked = true;
      cts.Cancel();
      return ValueTask.CompletedTask;
    });
    using var s2 = bus.Subscribe<HL>(_ => {
      secondInvoked = true;
      return ValueTask.CompletedTask;
    });

    await Assert.That(async () => await ((ISignalSink)bus).ReceiveAsync(new HL(1), cts.Token))
      .Throws<OperationCanceledException>();

    await Assert.That(firstInvoked).IsTrue();
    await Assert.That(secondInvoked).IsFalse()
      .Because("the InvokeAsync loop must honor cancellation between handler invocations");
  }
}
