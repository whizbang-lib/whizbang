using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage gaps left by <c>AsbDeadLetterDrainerWiringTests</c>: the transport-name identity,
/// the non-positive budget short-circuit, and the mid-pass break once the total cap is
/// exhausted before every active subscription has been visited.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusFleetDeadLetterDrainer.cs</code-under-test>
public class AzureServiceBusFleetDeadLetterDrainerCoverageTests {

  private sealed class _recordingDrainer((string TopicName, string SubscriptionName) key) : ITransportDeadLetterDrainer {
    public int Invocations;
    public int ReturnPerDrain { get; init; } = 1;
    public string TransportName => $"asb:{key.TopicName}/{key.SubscriptionName}";
    public Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
      Invocations++;
      return Task.FromResult(Math.Min(ReturnPerDrain, maxCount));
    }
  }

  /// <summary>
  /// Production risk if this regresses: the drain worker resolves every registered
  /// <see cref="ITransportDeadLetterDrainer"/> from DI and keys behavior off
  /// <see cref="ITransportDeadLetterDrainer.TransportName"/> — a wrong or empty name would
  /// misattribute this drainer's pass in any host running more than one broker.
  /// </summary>
  [Test]
  public async Task TransportName_IsAsbAsync() {
    var fleet = new AzureServiceBusFleetDeadLetterDrainer(
      () => new List<(string, string)>(),
      _ => throw new InvalidOperationException("no drain pass runs in this test"));

    await Assert.That(fleet.TransportName).IsEqualTo("asb");
  }

  /// <summary>
  /// Production risk if this regresses: without the short-circuit, a pass the worker asked to
  /// skip would still read the active-subscription snapshot and touch every subscription's
  /// cached drainer — needless management-plane and broker chatter on a tick meant to be a
  /// complete no-op.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_WithNonPositiveBudget_ReturnsZeroWithoutTouchingAnySubscriptionAsync() {
    var subscriptionSnapshotReads = 0;
    var fleet = new AzureServiceBusFleetDeadLetterDrainer(
      () => { subscriptionSnapshotReads++; return new List<(string, string)> { ("orders-topic", "orders-svc-sub") }; },
      _ => throw new InvalidOperationException("must not be reached with a non-positive budget"));

    var drained = await fleet.DrainDeadLetterQueueAsync(-1);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(subscriptionSnapshotReads).IsEqualTo(0)
      .Because("a non-positive budget must short-circuit before the active-subscription "
             + "snapshot is even read");
  }

  /// <summary>
  /// Production risk if this regresses: once the pass's total cap is spent, the fleet must stop
  /// rather than keep creating and invoking drainers for the remaining subscriptions — maxCount
  /// is a broker ops-rate pacing contract per <c>TransportDeadLetterDrainWorker.MaxPerTick</c>,
  /// and multiplying it by the subscription count reintroduces exactly the burst the pacing
  /// exists to prevent.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_BudgetExhaustedMidPass_StopsWithoutVisitingRemainingSubscriptionsAsync() {
    var subs = new List<(string TopicName, string SubscriptionName)> {
      ("t1", "s1"), ("t2", "s2"), ("t3", "s3"), ("t4", "s4"),
    };
    var made = new Dictionary<(string, string), _recordingDrainer>();
    var fleet = new AzureServiceBusFleetDeadLetterDrainer(
      () => subs,
      key => { var d = new _recordingDrainer(key) { ReturnPerDrain = 5 }; made[key] = d; return d; });

    var drained = await fleet.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(10);
    await Assert.That(made.Count).IsEqualTo(2)
      .Because("the budget is exhausted after the second subscription (5 + 5 = 10); the third "
             + "and fourth subscriptions must never get a drainer created for them");
  }
}
