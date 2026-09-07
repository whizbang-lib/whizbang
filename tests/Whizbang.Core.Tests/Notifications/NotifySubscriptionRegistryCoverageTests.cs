using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Coverage-round tests for <see cref="NotifySubscriptionRegistry"/> paths that
/// <see cref="NotifySubscriptionRegistryTests"/> doesn't reach: removing a subscription that was
/// never registered on an otherwise-populated channel, and the CAS-loop retry branches in
/// <see cref="NotifySubscriptionRegistry.Add"/> / <see cref="NotifySubscriptionRegistry.Remove"/>
/// that only fire when two callers race on the exact same dictionary transition.
/// </summary>
/// <remarks>
/// The retry-branch tests below force real OS-thread contention with a <see cref="Barrier"/> so
/// every racer starts as close to simultaneously as possible — the same intent as the existing
/// <c>Add_ConcurrentCallsToSameChannel_ExactlyOneReturnsTrueAsync</c> /
/// <c>Remove_ConcurrentCallsLeavingZero_ExactlyOneReturnsTrueAsync</c> tests, just with a tighter
/// race window. The asserted outcome (exactly one winner, correct final state) is always true
/// regardless of which internal retry branch actually fired, so these tests never flake — but
/// which exact source line the race exercises on a given run is not something a test can pin
/// down without a hook into the CAS loop itself.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Notifications/NotifySubscriptionRegistry.cs</code-under-test>
public class NotifySubscriptionRegistryCoverageTests {

  private sealed class FakeSubscription(string channel) : INotifySubscription {
    public string ChannelName { get; } = channel;
    public void OnNotification(string payload) { }
  }

  // If Remove ever returned true (or threw) for a subscription that was never on this channel's
  // list, a caller could issue a spurious UNLISTEN while a real subscriber is still registered —
  // the channel would go dark for everyone still on it.
  [Test]
  public async Task Remove_SubscriptionNeverAddedToAnExistingChannel_ReturnsFalseAndLeavesItIntactAsync() {
    var reg = new NotifySubscriptionRegistry();
    var registered = new FakeSubscription("shared-channel");
    var neverAdded = new FakeSubscription("shared-channel");
    reg.Add(registered);

    var wasLast = reg.Remove(neverAdded);

    await Assert.That(wasLast).IsFalse()
      .Because("the subscription was never on this channel's list, so removing it must be a safe no-op, not a false 'you just unlistened'");
    await Assert.That(reg.Get("shared-channel").Length).IsEqualTo(1)
      .Because("removing an unregistered subscription must not disturb the channel's real subscriber");
  }

  // Two Adds that both observe a channel as ABSENT before either finishes installing it must
  // race safely: the loser retries as an update instead of silently reporting wasFirst=false
  // against a channel that doesn't exist yet, which would leave nobody having issued LISTEN.
  [Test]
  public async Task Add_ManyThreadsRaceToCreateTheSameBrandNewChannel_ExactlyOneWinsAndAllAreRegisteredAsync() {
    var reg = new NotifySubscriptionRegistry();
    const int racers = 64;
    var subs = Enumerable.Range(0, racers).Select(_ => new FakeSubscription("brand-new-race-channel")).ToArray();
    var results = new bool[racers];
    using var barrier = new Barrier(racers);
    var threads = Enumerable.Range(0, racers).Select(i => new Thread(() => {
      barrier.SignalAndWait();
      results[i] = reg.Add(subs[i]);
    })).ToArray();
    foreach (var t in threads) { t.Start(); }
    foreach (var t in threads) { t.Join(); }

    await Assert.That(results.Count(r => r)).IsEqualTo(1)
      .Because("exactly one caller may issue LISTEN for a channel no matter how many threads race to create it");
    await Assert.That(reg.Get("brand-new-race-channel").Length).IsEqualTo(racers)
      .Because("every racer's subscription must still end up registered even though only one was first");
  }

  // Symmetric to the Add race: many threads draining the SAME channel down to zero must retry
  // safely on both the empty-removal CAS and the shrink-update CAS, or the channel could be torn
  // down (or left standing) inconsistently with who's actually still on it.
  [Test]
  public async Task Remove_ManyThreadsRaceToDrainTheSameChannel_ExactlyOneWinsAndChannelEndsEmptyAsync() {
    var reg = new NotifySubscriptionRegistry();
    const int racers = 64;
    var subs = Enumerable.Range(0, racers).Select(_ => new FakeSubscription("drain-race-channel")).ToArray();
    foreach (var s in subs) { reg.Add(s); }

    var results = new bool[racers];
    using var barrier = new Barrier(racers);
    var threads = Enumerable.Range(0, racers).Select(i => new Thread(() => {
      barrier.SignalAndWait();
      results[i] = reg.Remove(subs[i]);
    })).ToArray();
    foreach (var t in threads) { t.Start(); }
    foreach (var t in threads) { t.Join(); }

    await Assert.That(results.Count(r => r)).IsEqualTo(1)
      .Because("exactly one caller may issue UNLISTEN no matter how many threads race to drain the last subscriber");
    await Assert.That(reg.Get("drain-race-channel").Length).IsEqualTo(0);
    await Assert.That(reg.AllChannels()).DoesNotContain("drain-race-channel")
      .Because("a fully-drained channel must not linger as a phantom entry for the next Add to trip over");
  }
}
