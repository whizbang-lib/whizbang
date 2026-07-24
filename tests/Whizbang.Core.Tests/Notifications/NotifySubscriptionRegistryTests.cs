using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Locks the registration semantics that the shared notify connection's <c>LISTEN</c> /
/// <c>UNLISTEN</c> calls depend on: only the first subscribe-per-channel and the last
/// unsubscribe-per-channel are connection events. Slice 33.1.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class NotifySubscriptionRegistryTests {

  private sealed class FakeSubscription(string channel) : INotifySubscription {
    public string ChannelName { get; } = channel;
    public int CallCount;
    public void OnNotification(string payload) => Interlocked.Increment(ref CallCount);
  }

  [Test]
  public async Task Add_FirstSubscriberForChannel_ReturnsTrue_AndChannelIsListedAsync() {
    var reg = new NotifySubscriptionRegistry();
    var sub = new FakeSubscription("wh_work_i_test");

    var wasFirst = reg.Add(sub);

    await Assert.That(wasFirst).IsTrue();
    await Assert.That(reg.AllChannels()).Contains("wh_work_i_test");
    await Assert.That(reg.Get("wh_work_i_test")).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Add_SecondSubscriberForSameChannel_ReturnsFalse_AndBothSubscribersRegisteredAsync() {
    var reg = new NotifySubscriptionRegistry();
    var sub1 = new FakeSubscription("wh_committed");
    var sub2 = new FakeSubscription("wh_committed");

    _ = reg.Add(sub1);
    var wasFirstForSecond = reg.Add(sub2);

    await Assert.That(wasFirstForSecond).IsFalse();
    await Assert.That(reg.Get("wh_committed")).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Add_TwoDifferentChannels_BothReturnTrue_AndBothListedAsync() {
    var reg = new NotifySubscriptionRegistry();
    var subA = new FakeSubscription("ch_a");
    var subB = new FakeSubscription("ch_b");

    var firstA = reg.Add(subA);
    var firstB = reg.Add(subB);

    await Assert.That(firstA).IsTrue();
    await Assert.That(firstB).IsTrue();
    await Assert.That(reg.AllChannels()).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Remove_LastSubscriberForChannel_ReturnsTrue_AndChannelDisappearsAsync() {
    var reg = new NotifySubscriptionRegistry();
    var sub = new FakeSubscription("wh_committed");
    reg.Add(sub);

    var wasLast = reg.Remove(sub);

    await Assert.That(wasLast).IsTrue();
    await Assert.That(reg.AllChannels()).DoesNotContain("wh_committed");
    await Assert.That(reg.Get("wh_committed")).IsEmpty();
  }

  [Test]
  public async Task Remove_OneOfManySubscribers_ReturnsFalse_AndChannelStaysAsync() {
    var reg = new NotifySubscriptionRegistry();
    var sub1 = new FakeSubscription("wh_committed");
    var sub2 = new FakeSubscription("wh_committed");
    reg.Add(sub1);
    reg.Add(sub2);

    var wasLast = reg.Remove(sub1);

    await Assert.That(wasLast).IsFalse();
    await Assert.That(reg.AllChannels()).Contains("wh_committed");
    await Assert.That(reg.Get("wh_committed")).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Remove_UnregisteredSubscription_ReturnsFalseAndNoOpsAsync() {
    var reg = new NotifySubscriptionRegistry();
    var sub = new FakeSubscription("never_added");

    var wasLast = reg.Remove(sub);

    await Assert.That(wasLast).IsFalse();
    await Assert.That(reg.AllChannels()).IsEmpty();
  }

  [Test]
  public async Task Get_UnregisteredChannel_ReturnsEmptyAsync() {
    var reg = new NotifySubscriptionRegistry();

    await Assert.That(reg.Get("nothing")).IsEmpty();
  }

  [Test]
  public async Task TotalSubscriberCount_TracksAggregateAcrossChannelsAsync() {
    var reg = new NotifySubscriptionRegistry();
    reg.Add(new FakeSubscription("a"));
    reg.Add(new FakeSubscription("a"));
    reg.Add(new FakeSubscription("b"));

    await Assert.That(reg.TotalSubscriberCount()).IsEqualTo(3);
  }

  [Test]
  public async Task Add_ConcurrentCallsToSameChannel_ExactlyOneReturnsTrueAsync() {
    var reg = new NotifySubscriptionRegistry();
    var subs = Enumerable.Range(0, 100)
      .Select(_ => new FakeSubscription("contended"))
      .ToArray();

    var results = new bool[subs.Length];
    await Parallel.ForEachAsync(
      Enumerable.Range(0, subs.Length),
      (i, _) => { results[i] = reg.Add(subs[i]); return ValueTask.CompletedTask; });

    var firstCount = results.Count(r => r);
    await Assert.That(firstCount).IsEqualTo(1);
    await Assert.That(reg.Get("contended")).Count().IsEqualTo(100);
  }

  [Test]
  public async Task Remove_ConcurrentCallsLeavingZero_ExactlyOneReturnsTrueAsync() {
    var reg = new NotifySubscriptionRegistry();
    var subs = Enumerable.Range(0, 100)
      .Select(_ => new FakeSubscription("contended"))
      .ToArray();
    foreach (var s in subs) { reg.Add(s); }

    var results = new bool[subs.Length];
    await Parallel.ForEachAsync(
      Enumerable.Range(0, subs.Length),
      (i, _) => { results[i] = reg.Remove(subs[i]); return ValueTask.CompletedTask; });

    var lastCount = results.Count(r => r);
    await Assert.That(lastCount).IsEqualTo(1);
    await Assert.That(reg.Get("contended")).IsEmpty();
    await Assert.That(reg.AllChannels()).DoesNotContain("contended");
  }
}
