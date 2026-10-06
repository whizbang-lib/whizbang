// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for the all-perspectives <see cref="AppendAndWaitEventStoreDecorator"/> overload
/// called with no timeout: the wait must still be bounded, by the documented 30-second default,
/// both for the completion wait itself and in what the waiting callback reports.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/AppendAndWaitEventStoreDecorator.cs</code-under-test>
[Category("EventStore")]
[Category("Sync")]
public sealed class AppendAndWaitEventStoreDecoratorBranchCoverageTests {
  private sealed record TestEvent(string Value) : IEvent;

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_NullTimeout_WaitsWithTheDefaultTimeoutAsync() {
    var tracker = new FakeScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());
    var completionAwaiter = new CapturingEventCompletionAwaiter();
    var decorator = new AppendAndWaitEventStoreDecorator(
      inner: new InMemoryEventStore(),
      syncAwaiter: new UnusedPerspectiveSyncAwaiter(),
      eventCompletionAwaiter: completionAwaiter,
      scopedEventTracker: tracker);
    SyncWaitingContext? waiting = null;

    var result = await decorator.AppendAndWaitAsync(
      Guid.NewGuid(),
      new TestEvent("payload"),
      timeout: null,
      onWaiting: ctx => waiting = ctx);

    await Assert.That(completionAwaiter.LastTimeout).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("an unspecified timeout must fall back to the default rather than wait without bound");
    await Assert.That(waiting).IsNotNull();
    await Assert.That(waiting!.Timeout).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("the waiting callback reports the timeout actually in force");
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  private sealed class UnusedPerspectiveSyncAwaiter : IPerspectiveSyncAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new NotSupportedException("Not used by the all-perspectives overload");

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new NotSupportedException("Not used by the all-perspectives overload");

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) =>
      throw new NotSupportedException("Not used by the all-perspectives overload");
  }

  private sealed class CapturingEventCompletionAwaiter : IEventCompletionAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();
    public TimeSpan? LastTimeout { get; private set; }

    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, CancellationToken cancellationToken = default) {
      LastTimeout = timeout;
      return Task.FromResult(true);
    }

    public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) => true;
  }

  private sealed class FakeScopedEventTracker : IScopedEventTracker {
    private readonly List<TrackedEvent> _events = [];

    public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) =>
      _events.Add(new TrackedEvent(streamId, eventType, eventId));

    public IReadOnlyList<TrackedEvent> GetEmittedEvents() => _events;

    public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => _events;

    public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) =>
      _events.All(e => processedEventIds.Contains(e.EventId));
  }
}
