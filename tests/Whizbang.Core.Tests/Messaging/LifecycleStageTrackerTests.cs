using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for LifecycleStageTracker — shared singleton that prevents the same
/// message+stage combination from being processed by multiple workers.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/LifecycleStageTracker.cs</code-under-test>
public class LifecycleStageTrackerTests {

  [Test]
  public async Task TryClaim_FirstCall_ReturnsTrueAsync() {
    var tracker = new LifecycleStageTracker();
    var result = tracker.TryClaim(Guid.NewGuid(), LifecycleStage.PostInboxDetached);
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task TryClaim_SameMessageSameStage_ReturnsFalseAsync() {
    var tracker = new LifecycleStageTracker();
    var messageId = Guid.NewGuid();
    tracker.TryClaim(messageId, LifecycleStage.PostInboxDetached);

    var result = tracker.TryClaim(messageId, LifecycleStage.PostInboxDetached);
    await Assert.That(result).IsFalse()
      .Because("Same message+stage should not fire twice");
  }

  [Test]
  public async Task TryClaim_SameMessageDifferentStage_BothSucceedAsync() {
    var tracker = new LifecycleStageTracker();
    var messageId = Guid.NewGuid();

    var result1 = tracker.TryClaim(messageId, LifecycleStage.PostInboxDetached);
    var result2 = tracker.TryClaim(messageId, LifecycleStage.PostAllPerspectivesDetached);

    await Assert.That(result1).IsTrue();
    await Assert.That(result2).IsTrue()
      .Because("Different stages for same message should both fire");
  }

  [Test]
  public async Task TryClaim_DifferentMessages_BothSucceedAsync() {
    var tracker = new LifecycleStageTracker();

    var result1 = tracker.TryClaim(Guid.NewGuid(), LifecycleStage.PostInboxDetached);
    var result2 = tracker.TryClaim(Guid.NewGuid(), LifecycleStage.PostInboxDetached);

    await Assert.That(result1).IsTrue();
    await Assert.That(result2).IsTrue()
      .Because("Different messages should both fire at same stage");
  }

  [Test]
  public async Task Release_AllowsReclaimAsync() {
    var tracker = new LifecycleStageTracker();
    var messageId = Guid.NewGuid();
    var stage = LifecycleStage.PostInboxDetached;

    tracker.TryClaim(messageId, stage);
    tracker.Release(messageId, stage);

    var result = tracker.TryClaim(messageId, stage);
    await Assert.That(result).IsTrue()
      .Because("Released message+stage should be reclaimable (retry after failure)");
  }

  [Test]
  public async Task Purge_RemovesExpiredEntriesAsync() {
    var tracker = new LifecycleStageTracker();
    var messageId = Guid.NewGuid();
    tracker.TryClaim(messageId, LifecycleStage.PostInboxDetached);

    // Purge with zero max age — everything expires
    tracker.Purge(TimeSpan.Zero);

    var result = tracker.TryClaim(messageId, LifecycleStage.PostInboxDetached);
    await Assert.That(result).IsTrue()
      .Because("Purged entries should be reclaimable");
  }
}
