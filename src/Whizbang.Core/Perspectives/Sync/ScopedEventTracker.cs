using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Thread-safe implementation of <see cref="IScopedEventTracker"/> using a concurrent bag.
/// </summary>
/// <remarks>
/// <para>
/// This implementation is designed to be registered as a scoped service,
/// tracking events within a single request or operation scope.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs</tests>
public sealed class ScopedEventTracker : IScopedEventTracker {
  private readonly ConcurrentBag<TrackedEvent> _trackedEvents = new();

  /// <inheritdoc />
  public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) {
    ArgumentNullException.ThrowIfNull(eventType);
    _trackedEvents.Add(new TrackedEvent(streamId, eventType, eventId));
  }

  /// <inheritdoc />
  public IReadOnlyList<TrackedEvent> GetEmittedEvents() {
    return _trackedEvents.ToArray();
  }

  /// <inheritdoc />
  public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) {
    ArgumentNullException.ThrowIfNull(filter);

    var allEvents = _trackedEvents.ToArray();
    return allEvents.Where(e => _matchesFilter(e, filter)).ToArray();
  }

  /// <inheritdoc />
  public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) {
    ArgumentNullException.ThrowIfNull(filter);
    ArgumentNullException.ThrowIfNull(processedEventIds);

    var matchingEvents = GetEmittedEvents(filter);

    if (matchingEvents.Count == 0) {
      return true; // No events to wait for
    }

    return matchingEvents.All(e => processedEventIds.Contains(e.EventId));
  }

  private static bool _matchesFilter(TrackedEvent evt, SyncFilterNode filter) {
    return filter switch {
      StreamFilter sf => evt.StreamId == sf.StreamId,
      EventTypeFilter etf => etf.EventTypes.Contains(evt.EventType),
      CurrentScopeFilter => true, // All events in this tracker are in current scope
      AllPendingFilter => true,   // Match all events
      AndFilter af => _matchesFilter(evt, af.Left) && _matchesFilter(evt, af.Right),
      OrFilter of => _matchesFilter(evt, of.Left) || _matchesFilter(evt, of.Right),
      _ => false
    };
  }
}
