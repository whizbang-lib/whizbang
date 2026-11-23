// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Every wait the tracker starts announces the events it waits on (#2247), so the perspective worker flushes a
/// write-behind model holding one of them at once instead of letting the wait sit out the flush interval.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
public class SyncEventTrackerWaitStartedTests {
  private const string PERSPECTIVE = "Tests.Counter";

  [Test]
  public async Task EveryKindOfWait_AnnouncesItsEventsAsync() {
    var tracker = new SyncEventTracker();
    var announced = new List<IReadOnlyList<Guid>>();
    tracker.WaitStarted += announced.Add;
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    using var cancel = new CancellationTokenSource();

    await tracker.WaitForEventsAsync([a], TimeSpan.Zero);
    await tracker.WaitForPerspectiveEventsAsync([b], PERSPECTIVE, TimeSpan.Zero);
    await tracker.WaitForAllPerspectivesAsync([a, b], TimeSpan.Zero);
    var applied = tracker.WhenAppliedAsync(a, PERSPECTIVE, cancel.Token);
    await cancel.CancelAsync();
    await Assert.That(async () => await applied).Throws<OperationCanceledException>();

    await Assert.That(announced).Count().IsEqualTo(4);
    await Assert.That(announced[0]).IsEquivalentTo([a]);
    await Assert.That(announced[1]).IsEquivalentTo([b]);
    await Assert.That(announced[2]).IsEquivalentTo([a, b]);
    await Assert.That(announced[3]).IsEquivalentTo([a]);
  }

  [Test]
  public async Task AWaitOnNothing_OrAlreadyCanceled_AnnouncesNothingAsync() {
    var tracker = new SyncEventTracker();
    var announced = 0;
    tracker.WaitStarted += _ => announced++;
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await tracker.WaitForEventsAsync([], TimeSpan.Zero);
    await tracker.WaitForPerspectiveEventsAsync([], PERSPECTIVE, TimeSpan.Zero);
    await tracker.WaitForAllPerspectivesAsync([], TimeSpan.Zero);
    await Assert.That(async () => await tracker.WhenAppliedAsync(Guid.CreateVersion7(), PERSPECTIVE, canceled.Token))
      .Throws<OperationCanceledException>();

    await Assert.That(announced).IsEqualTo(0);
  }

  [Test]
  public async Task TheDefaultTracker_IsAnAnnouncerAsync() {
    await Assert.That(new SyncEventTracker() is ISyncWaitAnnouncer).IsTrue()
      .Because("the worker listens through the announcer; a tracker that is not one gets no early flush");
  }
}
