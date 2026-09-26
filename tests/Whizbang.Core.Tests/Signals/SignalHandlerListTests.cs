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
    var bus = new SignalBus(transports: [], pullSources: []);
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
    var bus = new SignalBus(transports: [], pullSources: []);
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

  // A subscription hands back exactly the delegate it registered and disposes at most once, so
  // no production caller can ask this list to remove something it does not hold. The guard is
  // what makes that safe anyway: without it the removal would size the replacement array off an
  // index of -1 and take the whole handler set down with it — losing every live subscriber on
  // the bus because of one stray removal.
  [Test]
  public async Task Remove_HandlerThatWasNeverAdded_LeavesTheRegisteredHandlersIntactAsync() {
    var list = new SignalHandlerList<HL>();
    var invocations = 0;
    using var registered = list.Add(_ => { Interlocked.Increment(ref invocations); return ValueTask.CompletedTask; });

    list.Remove(_ => ValueTask.CompletedTask); // a delegate this list has never seen

    await list.InvokeAsync(new HL(1), CancellationToken.None);

    await Assert.That(invocations).IsEqualTo(1)
      .Because("removing a handler the list does not hold must leave the ones it does hold alone; "
        + "anything else drops live subscribers on the strength of a bad removal");
  }
}
