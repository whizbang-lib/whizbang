// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  /// <summary>
  /// A non-positive ceiling is not "retain nothing": it falls back to the default, so claims are
  /// kept and a second claim of the same stage is still refused.
  /// </summary>
  [Test]
  [Arguments(0)]
  [Arguments(-5)]
  public async Task Ctor_NonPositiveCeiling_FallsBackToTheDefaultAndKeepsClaimsAsync(int ceiling) {
    var tracker = new LifecycleStageTracker(maxTrackedClaims: ceiling);
    var first = Guid.CreateVersion7();

    await Assert.That(tracker.TryClaim(first, LifecycleStage.PostInboxDetached)).IsTrue();
    await Assert.That(tracker.TryClaim(Guid.CreateVersion7(), LifecycleStage.PostInboxDetached)).IsTrue();
    await Assert.That(tracker.TryClaim(Guid.CreateVersion7(), LifecycleStage.PostInboxDetached)).IsTrue();

    await Assert.That(tracker.TrackedClaims).IsEqualTo(3);
    await Assert.That(tracker.TryClaim(first, LifecycleStage.PostInboxDetached)).IsFalse()
      .Because("an evicted claim would fire the stage twice");
  }
}
