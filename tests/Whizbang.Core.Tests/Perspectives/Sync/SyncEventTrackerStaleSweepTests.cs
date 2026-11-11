// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// <para>An entry in the perspective sync tracker leaves when this instance applies the event, and otherwise only
/// through the stale sweep (#2228). When the perspective runs on another instance, or the event fails or is
/// dead-lettered, nothing on this instance ever applies it, so the sweep is the entry's only end. The sweep reads
/// the injected clock, so its bound is testable, and the tracker answers "is this event still pending on any
/// perspective" from an index by event id rather than by scanning every tracked key.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/SyncEventTracker.cs</code-under-test>
public sealed class SyncEventTrackerStaleSweepTests {
  private const string PERSPECTIVE_A = "Orders.Summary";
  private const string PERSPECTIVE_B = "Orders.Search";
  private static readonly TimeSpan _maxAge = TimeSpan.FromMinutes(5);

  private sealed record SweepEvent;

  [Test]
  public async Task CleanupStaleEntries_RemovesAnEntryOnlyOnceTheInjectedClockPassesItsAgeAsync() {
    var clock = new FakeTimeProvider();
    var tracker = new SyncEventTracker(clock);
    var eventId = Guid.NewGuid();
    tracker.TrackEvent(typeof(SweepEvent), eventId, Guid.NewGuid(), PERSPECTIVE_A);

    clock.Advance(_maxAge - TimeSpan.FromSeconds(1));
    await Assert.That(tracker.CleanupStaleEntries(_maxAge)).IsEqualTo(0)
      .Because("an entry younger than the bound may still be applied here");

    clock.Advance(TimeSpan.FromSeconds(1));
    await Assert.That(tracker.CleanupStaleEntries(_maxAge)).IsEqualTo(1)
      .Because("an entry no instance here applied within the bound is the sweep's to end, or it is held forever");
    await Assert.That(tracker.TrackedEntryCount).IsEqualTo(0);
    await Assert.That(tracker.GetAllTrackedEventIds()).IsEmpty();
  }

  [Test]
  public async Task CleanupStaleEntries_SignalsTheWaitersOfTheEntriesItEndsAsync() {
    var clock = new FakeTimeProvider();
    var tracker = new SyncEventTracker(clock);
    var eventId = Guid.NewGuid();
    tracker.TrackEvent(typeof(SweepEvent), eventId, Guid.NewGuid(), PERSPECTIVE_A);
    var perspectiveWait = tracker.WaitForPerspectiveEventsAsync([eventId], PERSPECTIVE_A, Timeout.InfiniteTimeSpan);
    var allWait = tracker.WaitForAllPerspectivesAsync([eventId], Timeout.InfiniteTimeSpan);
    var eventWait = tracker.WaitForEventsAsync([eventId], Timeout.InfiniteTimeSpan);

    clock.Advance(_maxAge);
    tracker.CleanupStaleEntries(_maxAge);

    await Assert.That(await perspectiveWait).IsTrue();
    await Assert.That(await allWait).IsTrue();
    await Assert.That(await eventWait).IsTrue();
  }

  [Test]
  public async Task MarkProcessedByPerspective_KeepsTheEventPending_UntilItsLastPerspectiveIsMarkedAsync() {
    var tracker = new SyncEventTracker(new FakeTimeProvider());
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEvent(typeof(SweepEvent), eventId, streamId, PERSPECTIVE_A);
    tracker.TrackEvent(typeof(SweepEvent), eventId, streamId, PERSPECTIVE_B);
    var allWait = tracker.WaitForAllPerspectivesAsync([eventId], Timeout.InfiniteTimeSpan);

    tracker.MarkProcessedByPerspective([eventId], PERSPECTIVE_A);
    await Assert.That(allWait.IsCompleted).IsFalse()
      .Because("the event is still pending on its other perspective");
    await Assert.That(tracker.GetAllTrackedEventIds()).Contains(eventId);

    tracker.MarkProcessedByPerspective([eventId], PERSPECTIVE_B);
    await Assert.That(await allWait).IsTrue();
    await Assert.That(tracker.TrackedEntryCount).IsEqualTo(0);
  }

  [Test]
  public async Task MarkProcessed_RemovesEveryPerspectiveOfTheEvent_AndLeavesOtherEventsAsync() {
    var tracker = new SyncEventTracker(new FakeTimeProvider());
    var eventId = Guid.NewGuid();
    var otherId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEvent(typeof(SweepEvent), eventId, streamId, PERSPECTIVE_A);
    tracker.TrackEvent(typeof(SweepEvent), eventId, streamId, PERSPECTIVE_B);
    tracker.TrackEvent(typeof(SweepEvent), otherId, streamId, PERSPECTIVE_A);

    tracker.MarkProcessed([eventId]);

    await Assert.That(tracker.GetAllTrackedEventIds()).IsEquivalentTo([otherId]);
    await Assert.That(tracker.TrackedEntryCount).IsEqualTo(1);
  }

  [Test]
  public async Task TrackEvent_SameEventAndPerspectiveTwice_KeepsTheFirstEntryAsync() {
    var clock = new FakeTimeProvider();
    var tracker = new SyncEventTracker(clock);
    var eventId = Guid.NewGuid();
    tracker.TrackEvent(typeof(SweepEvent), eventId, Guid.NewGuid(), PERSPECTIVE_A);
    clock.Advance(_maxAge);
    tracker.TrackEvent(typeof(SweepEvent), eventId, Guid.NewGuid(), PERSPECTIVE_A);

    await Assert.That(tracker.TrackedEntryCount).IsEqualTo(1);
    await Assert.That(tracker.CleanupStaleEntries(_maxAge)).IsEqualTo(1)
      .Because("a repeated track does not refresh the entry's age");
  }

  [Test]
  public async Task MarkProcessedByPerspective_ForAPerspectiveNotTracked_LeavesTheEventsOtherEntriesAsync() {
    var tracker = new SyncEventTracker(new FakeTimeProvider());
    var eventId = Guid.NewGuid();
    tracker.TrackEvent(typeof(SweepEvent), eventId, Guid.NewGuid(), PERSPECTIVE_A);

    tracker.MarkProcessedByPerspective([eventId, Guid.NewGuid()], PERSPECTIVE_B);

    await Assert.That(tracker.TrackedEntryCount).IsEqualTo(1)
      .Because("marking a perspective the event is not tracked for, or an event not tracked at all, removes nothing");
    await Assert.That(tracker.GetAllTrackedEventIds()).Contains(eventId);
  }
}
