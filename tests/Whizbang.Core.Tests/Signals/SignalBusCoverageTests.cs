using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Covers <see cref="SignalBus.ReceiveAsync{TSignal}"/> when NO subscriber is registered for the
/// signal type — every existing <c>SignalBusTests</c> case subscribes first, so the "nobody is
/// listening" no-op path is never reached there.
/// </summary>
public class SignalBusCoverageTests {
  private readonly record struct UnsubscribedSignal(int Value) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  /// <summary>
  /// If this fell through to a lookup that threw (or blocked) on a missing type instead of a plain
  /// no-op, an ordinary race — a doorbell arriving before any subscriber for a brand-new signal
  /// type has registered — would fault the delivering transport's receive loop instead of simply
  /// discarding a signal nobody was listening for yet.
  /// </summary>
  [Test]
  public async Task ReceiveAsync_NoSubscriberRegistered_CompletesAsNoOpAsync() {
    SignalBus bus = new([]);

    var receive = bus.ReceiveAsync(new UnsubscribedSignal(42), CancellationToken.None);

    await Assert.That(receive.IsCompletedSuccessfully).IsTrue()
      .Because("a signal with no registered subscriber must resolve as a synchronous no-op, not "
             + "fault or hang the delivering transport's receive loop");
    await receive; // would throw here if it were faulted instead
  }
}
