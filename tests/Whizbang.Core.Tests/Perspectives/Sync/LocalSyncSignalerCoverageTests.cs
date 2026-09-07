using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Targeted coverage for the <c>Subscription.Dispose</c> double-dispose guard — a branch the
/// broader <see cref="PerspectiveSyncSignalerTests"/> suite never reaches because it disposes
/// each subscription exactly once.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/LocalSyncSignaler.cs</code-under-test>
public class LocalSyncSignalerCoverageTests {

  [Test]
  public async Task Subscription_DisposedTwice_SecondCallIsANoOpAsync() {
    // A caller that disposes defensively (e.g. both an explicit unsubscribe and a `using` block)
    // must not have the second Dispose() rebuild/corrupt the handler bag for every OTHER live
    // subscriber on the same perspective type — that's a shared ConcurrentBag, so a buggy second
    // pass here would risk dropping a sibling subscriber's handler.
    using var signaler = new LocalSyncSignaler();
    var perspectiveType = typeof(string);
    var receivedCount = 0;

    var subscription = signaler.Subscribe(perspectiveType, _ => receivedCount++);
    subscription.Dispose();
    subscription.Dispose(); // second call must be inert, not throw or corrupt shared state

    signaler.SignalCheckpointUpdated(perspectiveType, Guid.NewGuid(), Guid.NewGuid());

    await Assert.That(receivedCount).IsEqualTo(0)
      .Because("the subscription was already removed by the first Dispose(); the second must not re-add or otherwise resurrect it");
  }

  [Test]
  public async Task Subscription_DisposedTwice_SiblingSubscriberOnSamePerspectiveStillReceivesSignalsAsync() {
    using var signaler = new LocalSyncSignaler();
    var perspectiveType = typeof(int);
    var siblingReceived = 0;

    var toDispose = signaler.Subscribe(perspectiveType, _ => { });
    var sibling = signaler.Subscribe(perspectiveType, _ => siblingReceived++);

    toDispose.Dispose();
    toDispose.Dispose(); // redundant dispose must not disturb the sibling's registration

    signaler.SignalCheckpointUpdated(perspectiveType, Guid.NewGuid(), Guid.NewGuid());

    await Assert.That(siblingReceived).IsEqualTo(1)
      .Because("a redundant Dispose() on one subscription must never drop another live subscriber sharing the same handler bag");
    sibling.Dispose();
  }
}
