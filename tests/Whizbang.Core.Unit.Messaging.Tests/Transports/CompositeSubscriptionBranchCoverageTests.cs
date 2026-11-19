// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Branch coverage for <see cref="CompositeSubscription"/>'s disconnect forwarding when nobody has
/// subscribed to the composite's own event. The underlying broker raises the disconnect on its own
/// callback thread, so forwarding into an empty handler list must be a no-op rather than a throw,
/// and must leave the forwarding wired for a handler attached later.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Transports/CompositeSubscription.cs</code-under-test>
public class CompositeSubscriptionBranchCoverageTests {
  private sealed class FakeSubscription : ISubscription {
    public bool IsActive => true;

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public Task PauseAsync() => Task.CompletedTask;

    public Task ResumeAsync() => Task.CompletedTask;

    public void Dispose() { }

    public void RaiseDisconnected(SubscriptionDisconnectedEventArgs args) => OnDisconnected?.Invoke(this, args);
  }

  [Test]
  public async Task OnDisconnected_NoCompositeHandler_IsANoOpAndLaterHandlersStillReceiveAsync() {
    var inner = new FakeSubscription();
    using var composite = new CompositeSubscription([inner]);

    // Nobody listens on the composite yet: the forward must not throw into the broker's callback.
    inner.RaiseDisconnected(new SubscriptionDisconnectedEventArgs { Reason = "unobserved" });

    SubscriptionDisconnectedEventArgs? received = null;
    composite.OnDisconnected += (_, args) => received = args;
    var second = new SubscriptionDisconnectedEventArgs { Reason = "observed" };
    inner.RaiseDisconnected(second);

    await Assert.That(received).IsSameReferenceAs(second)
      .Because("an unobserved disconnect must not unwire forwarding for a handler attached afterwards");
  }
}
