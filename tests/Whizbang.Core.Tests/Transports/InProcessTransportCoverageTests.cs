using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Coverage round 23 — targeted tests for <see cref="InProcessTransport"/> lines the existing
/// Whizbang.Transports.Tests suite does not exercise from Whizbang.Core.Tests:
/// <see cref="InProcessTransport.InitializeAsync"/> / <see cref="InProcessTransport.IsInitialized"/>,
/// and the private <c>InProcessSubscription</c>'s <c>OnDisconnected</c> event <c>remove</c> accessor.
/// </summary>
public class InProcessTransportCoverageTests {

  /// <summary>
  /// If InitializeAsync stops flipping <see cref="InProcessTransport.IsInitialized"/>, every
  /// readiness/health check that defaults to it (<c>ITransport.CheckConnectivityAsync</c>'s default
  /// implementation returns <c>IsInitialized</c>) reports "not connected" forever even though the
  /// transport is fully functional — a host would never become ready, or a health probe would flap
  /// a perfectly healthy in-process transport.
  /// </summary>
  [Test]
  public async Task InitializeAsync_ThenIsInitialized_ReportsTrueAsync() {
    var transport = new InProcessTransport();

    await Assert.That(transport.IsInitialized).IsFalse()
      .Because("before InitializeAsync runs, the transport must not claim to be ready");

    await transport.InitializeAsync(CancellationToken.None);

    await Assert.That(transport.IsInitialized).IsTrue()
      .Because("InitializeAsync is what marks the in-process transport ready for publish/subscribe");
  }

  /// <summary>
  /// The <c>OnDisconnected</c> event exists solely so callers written against a transport that DOES
  /// raise it (a real broker reconnecting) can use the in-process transport interchangeably. If
  /// detaching a handler here ever threw or corrupted subscription state, a caller's normal
  /// <c>+=</c>/<c>-=</c> (or <c>using</c>-scoped unsubscribe) pairing would blow up against this
  /// transport specifically, even though nothing about the subscription itself is unhealthy.
  /// </summary>
  [Test]
  public async Task Subscription_OnDisconnectedRemove_DetachesHandlerWithoutThrowingAsync() {
    var transport = new InProcessTransport();
    EventHandler<SubscriptionDisconnectedEventArgs> handler = (_, _) => { };

    var subscription = await transport.SubscribeBatchAsync(
      batchHandler: (_, _) => Task.CompletedTask,
      destination: new TransportDestination("coverage-destination"),
      batchOptions: new TransportBatchOptions());

    Exception? caught = null;
    try {
      subscription.OnDisconnected += handler;
      subscription.OnDisconnected -= handler;
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNull()
      .Because("detaching a disconnect handler must never throw — a throwing remove accessor would " +
               "break every caller's dispose/unsubscribe path");
    await Assert.That(subscription.IsActive).IsTrue()
      .Because("attaching/detaching a disconnect handler must not perturb the subscription's own active state");

    subscription.Dispose();
  }
}
