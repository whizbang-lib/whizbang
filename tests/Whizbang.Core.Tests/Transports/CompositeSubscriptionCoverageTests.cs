using System;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tail-of-round coverage for <see cref="CompositeSubscription"/>: the double-dispose guard and
/// the disconnect-event forwarding that the constructor wires up between every mirrored
/// namespace subscription and this handle's own event.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Transports/CompositeSubscription.cs</code-under-test>
public class CompositeSubscriptionCoverageTests {

  private sealed class _FakeSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
    public int DisposeCallCount { get; private set; }

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }

    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }

    public void Dispose() => DisposeCallCount++;

    public void RaiseDisconnected(SubscriptionDisconnectedEventArgs args) => OnDisconnected?.Invoke(this, args);
  }

  /// <summary>
  /// A mirror that disposes its underlying per-namespace subscriptions a second time risks a
  /// second broker-side Dispose() throwing, or double-releasing a resource a still-open
  /// pause/resume caller depends on — the guard exists so callers can dispose defensively.
  /// </summary>
  [Test]
  public async Task Dispose_CalledTwice_OnlyDisposesUnderlyingSubscriptionsOnceAsync() {
    var inner = new _FakeSubscription();
    var composite = new CompositeSubscription([inner]);

    composite.Dispose();
    composite.Dispose();

    await Assert.That(inner.DisposeCallCount).IsEqualTo(1)
      .Because("double-disposing the mirrored namespace subscriptions risks a broker-side throw or "
             + "a double release of a resource still in use");
  }

  /// <summary>
  /// If one namespace mirror drops without the composite forwarding the event, nothing tells the
  /// consumer its inbox mirroring across that transport traffic class has stopped — the service
  /// keeps reporting healthy while actually blind to that namespace's messages.
  /// </summary>
  [Test]
  public async Task OnDisconnected_WhenAnUnderlyingSubscriptionDisconnects_ForwardsTheEventAsync() {
    var inner = new _FakeSubscription();
    var composite = new CompositeSubscription([inner]);
    SubscriptionDisconnectedEventArgs? received = null;
    object? sender = null;
    composite.OnDisconnected += (s, args) => {
      sender = s;
      received = args;
    };

    var raised = new SubscriptionDisconnectedEventArgs { Reason = "broker-reset" };
    inner.RaiseDisconnected(raised);

    await Assert.That(received).IsSameReferenceAs(raised)
      .Because("a dropped mirror must surface as the composite's own disconnect so reconnection "
             + "logic bound to the composite actually fires");
    await Assert.That(sender).IsSameReferenceAs(composite)
      .Because("the forwarded event must read as coming from the handle the consumer holds, not "
             + "from the internal per-namespace subscription it never sees");
  }
}
