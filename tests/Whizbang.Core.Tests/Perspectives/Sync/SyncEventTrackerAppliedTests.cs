// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// The in-process half of waiting for an applied event (#959): the perspective worker marks an event applied
/// when its apply commits, and a waiter for that event and perspective wakes, whether or not the event was
/// ever tracked in this process.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/SyncEventTracker.cs</code-under-test>
public class SyncEventTrackerAppliedTests {
  private const string PERSPECTIVE = "Orders.OrderPerspective";

  [Test]
  public async Task WhenApplied_ForAnUntrackedEvent_CompletesOnlyWhenMarkedAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.CreateVersion7();

    var waiting = tracker.WhenAppliedAsync(eventId, PERSPECTIVE, CancellationToken.None);
    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("the event arrived from elsewhere and was never tracked here, which must not read as applied");

    tracker.MarkApplied([eventId], PERSPECTIVE);

    await waiting;
    await Assert.That(waiting.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task WhenApplied_AfterTheEventWasMarked_CompletesAtOnceAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.CreateVersion7();
    tracker.MarkApplied([eventId], PERSPECTIVE);

    var waiting = tracker.WhenAppliedAsync(eventId, PERSPECTIVE, CancellationToken.None);

    await Assert.That(waiting.IsCompletedSuccessfully).IsTrue()
      .Because("a wait that starts just after a local apply must not sit out the ledger's flush");
  }

  [Test]
  public async Task WhenApplied_MarkedForAnotherPerspective_StaysPendingAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.CreateVersion7();
    using var cts = new CancellationTokenSource();

    var waiting = tracker.WhenAppliedAsync(eventId, PERSPECTIVE, cts.Token);
    tracker.MarkApplied([eventId], "Orders.OtherPerspective");

    await Assert.That(waiting.IsCompleted).IsFalse();
    await cts.CancelAsync();
    await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task WhenApplied_TwoWaitersForOneEvent_BothWakeAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.CreateVersion7();

    var first = tracker.WhenAppliedAsync(eventId, PERSPECTIVE, CancellationToken.None);
    var second = tracker.WhenAppliedAsync(eventId, PERSPECTIVE, CancellationToken.None);
    tracker.MarkApplied([eventId], PERSPECTIVE);

    await Task.WhenAll(first, second);
    await Assert.That(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task WhenApplied_Canceled_UnregistersSoALaterMarkIsHarmlessAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.CreateVersion7();
    using var cts = new CancellationTokenSource();

    var waiting = tracker.WhenAppliedAsync(eventId, PERSPECTIVE, cts.Token);
    await Assert.That(tracker.AppliedWaiterCount).IsEqualTo(1)
      .Because("the wait is registered until it is served or gives up");
    await cts.CancelAsync();

    await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
    await Assert.That(tracker.AppliedWaiterCount).IsEqualTo(0)
      .Because("a waiter that gave up must not stay registered for an event that may never come");
    tracker.MarkApplied([eventId], PERSPECTIVE);
  }

  [Test]
  public async Task WhenApplied_WithAnAlreadyCanceledToken_IsCanceledAsync() {
    var tracker = new SyncEventTracker();

    var waiting = tracker.WhenAppliedAsync(Guid.CreateVersion7(), PERSPECTIVE, new CancellationToken(canceled: true));

    await Assert.That(waiting.IsCanceled).IsTrue();
    await Assert.That(tracker.AppliedWaiterCount).IsEqualTo(0);
  }

  [Test]
  public async Task MarkApplied_RemembersABoundedNumberOfRecentEventsAsync() {
    var tracker = new SyncEventTracker();
    var oldest = Guid.CreateVersion7();
    tracker.MarkApplied([oldest], PERSPECTIVE);

    tracker.MarkApplied(
      Enumerable.Range(0, SyncEventTracker.RECENTLY_APPLIED_CAPACITY).Select(_ => Guid.CreateVersion7()),
      PERSPECTIVE);
    using var cts = new CancellationTokenSource();
    var waiting = tracker.WhenAppliedAsync(oldest, PERSPECTIVE, cts.Token);

    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("the memory of recent applies is bounded; an evicted event is answered by the ledger instead");
    await cts.CancelAsync();
  }

  [Test]
  public async Task MarkApplied_SameEventTwice_IsRememberedOnceAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.CreateVersion7();

    tracker.MarkApplied([eventId, eventId], PERSPECTIVE);

    await Assert.That(tracker.RecentlyAppliedCount).IsEqualTo(1);
  }

  [Test]
  public async Task MarkApplied_WithNullArguments_ThrowsAsync() {
    var tracker = new SyncEventTracker();

    await Assert.That(() => tracker.MarkApplied(null!, PERSPECTIVE)).Throws<ArgumentNullException>();
    await Assert.That(() => tracker.MarkApplied([Guid.CreateVersion7()], null!)).Throws<ArgumentNullException>();
    await Assert.That(() => tracker.WhenAppliedAsync(Guid.CreateVersion7(), null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DefaultInterfaceMembers_MarkNothing_AndWaitUntilCanceledAsync() {
    ISyncEventTracker tracker = new MinimalTracker();
    using var cts = new CancellationTokenSource();

    tracker.MarkApplied([Guid.CreateVersion7()], PERSPECTIVE);
    var waiting = tracker.WhenAppliedAsync(Guid.CreateVersion7(), PERSPECTIVE, cts.Token);

    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("a tracker with no applied signal never claims an apply; the ledger answers instead");
    await cts.CancelAsync();
    await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
  }

  /// <summary>An <see cref="ISyncEventTracker"/> that implements only the members it must.</summary>
  private sealed class MinimalTracker : ISyncEventTracker {
    public void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName) { }
    public IReadOnlyList<TrackedSyncEvent> GetPendingEvents(Guid streamId, string perspectiveName, Type[]? eventTypes = null) => [];
    public void MarkProcessed(IEnumerable<Guid> eventIds) { }
    public IReadOnlyList<Guid> GetAllTrackedEventIds() => [];
    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName) { }
    public Task<bool> WaitForPerspectiveEventsAsync(IReadOnlyList<Guid> eventIds, string perspectiveName, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> WaitForAllPerspectivesAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public void UnregisterAwaiter(Guid awaiterId) { }
    public int CleanupStaleEntries(TimeSpan maxAge) => 0;
    public void MarkPerspectiveStreamProcessed(string perspectiveName, Guid streamId) { }
  }
}
