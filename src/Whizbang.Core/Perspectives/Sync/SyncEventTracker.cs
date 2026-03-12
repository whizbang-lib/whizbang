using System.Collections.Concurrent;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Thread-safe singleton implementation of event tracking for perspective sync.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="ConcurrentDictionary{TKey, TValue}"/> for thread-safe access
/// from multiple request scopes simultaneously.
/// </para>
/// <para>
/// Events are tracked from emit time until confirmed processed by the database,
/// enabling cross-scope synchronization.
/// </para>
/// <para>
/// <strong>Event-driven completion:</strong> Callers can use <see cref="WaitForEventsAsync"/>
/// to wait for specific events to be processed. When <see cref="MarkProcessed"/> is called,
/// all waiters for those events are automatically notified.
/// </para>
/// <para>
/// <strong>Per-awaiter tracking:</strong> Waiter registrations are keyed by awaiter ID,
/// enabling precise cleanup via <see cref="UnregisterAwaiter"/> when an awaiter is
/// cancelled — without affecting other awaiters waiting on the same events.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync#tracker-implementation</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncEventTrackerTests.cs</tests>
public sealed class SyncEventTracker : ISyncEventTracker {
  // Key is (eventId, perspectiveName) to allow the same event to be tracked for multiple perspectives
  private readonly ConcurrentDictionary<(Guid EventId, string PerspectiveName), TrackedSyncEvent> _trackedEvents = new();

  // Waiters keyed by (outerKey, awaiterId) → TCS.
  // Inner ConcurrentDictionary is keyed by AwaiterId for O(1) removal on cancellation.

  // Used by WaitForEventsAsync / MarkProcessed — outer key is eventId
  private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, TaskCompletionSource<bool>>> _eventWaiters = new();

  // Used by WaitForPerspectiveEventsAsync / MarkProcessedByPerspective — outer key is (eventId, perspectiveName)
  private readonly ConcurrentDictionary<(Guid EventId, string PerspectiveName), ConcurrentDictionary<Guid, TaskCompletionSource<bool>>> _perspectiveWaiters = new();

  // Used by WaitForAllPerspectivesAsync / MarkProcessedByPerspective — outer key is eventId
  private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, TaskCompletionSource<bool>>> _allPerspectivesWaiters = new();

  /// <inheritdoc />
  public void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName) {
    var tracked = new TrackedSyncEvent(eventType, eventId, streamId, perspectiveName, DateTime.UtcNow);
    _trackedEvents.TryAdd((eventId, perspectiveName), tracked);
  }

  /// <inheritdoc />
  public IReadOnlyList<TrackedSyncEvent> GetPendingEvents(
      Guid streamId,
      string perspectiveName,
      Type[]? eventTypes = null) {
    var query = _trackedEvents.Values
        .Where(e => e.StreamId == streamId && e.PerspectiveName == perspectiveName);

    if (eventTypes is { Length: > 0 }) {
      var typeSet = eventTypes.ToHashSet();
      query = query.Where(e => typeSet.Contains(e.EventType));
    }

    return query.ToList();
  }

  /// <inheritdoc />
  public void MarkProcessed(IEnumerable<Guid> eventIds) {
    foreach (var id in eventIds) {
      // Remove entries for ALL perspectives that have this eventId
      var keysToRemove = _trackedEvents.Keys.Where(k => k.EventId == id).ToList();
      foreach (var key in keysToRemove) {
        _trackedEvents.TryRemove(key, out _);
      }

      // Signal all waiters for this event and remove the outer entry
      if (_eventWaiters.TryRemove(id, out var waiters)) {
        foreach (var kvp in waiters) {
          kvp.Value.TrySetResult(true);
        }
      }
    }
  }

  /// <inheritdoc />
  public IReadOnlyList<Guid> GetAllTrackedEventIds() {
    return _trackedEvents.Keys.Select(k => k.EventId).Distinct().ToList();
  }

  /// <inheritdoc />
  public Task<bool> WaitForEventsAsync(
      IReadOnlyList<Guid> eventIds,
      TimeSpan timeout,
      Guid? awaiterId = null,
      CancellationToken cancellationToken = default) {
    if (eventIds is null || eventIds.Count == 0) {
      return Task.FromResult(true);
    }

    var resolvedAwaiterId = awaiterId ?? TrackedGuid.NewMedo();
    var pendingKeys = eventIds
        .Where(id => _trackedEvents.Keys.Any(k => k.EventId == id))
        .ToList();

    return _waitForCompletionAsync(
        _eventWaiters,
        pendingKeys,
        key => _trackedEvents.Keys.Any(k => k.EventId == key),
        resolvedAwaiterId,
        timeout,
        cancellationToken);
  }

  /// <inheritdoc />
  public void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName) {
    foreach (var id in eventIds) {
      // Remove entry for THIS specific perspective only
      var key = (id, perspectiveName);
      _trackedEvents.TryRemove(key, out _);

      // Signal perspective-specific waiters
      if (_perspectiveWaiters.TryRemove(key, out var perspectiveWaiters)) {
        foreach (var kvp in perspectiveWaiters) {
          kvp.Value.TrySetResult(true);
        }
      }

      // Check if ALL perspectives for this event are now processed
      var hasRemainingPerspectives = _trackedEvents.Keys.Any(k => k.EventId == id);

      if (!hasRemainingPerspectives && _allPerspectivesWaiters.TryRemove(id, out var allWaiters)) {
        // Signal all-perspectives waiters
        foreach (var kvp in allWaiters) {
          kvp.Value.TrySetResult(true);
        }
      }
    }
  }

  /// <inheritdoc />
  public Task<bool> WaitForPerspectiveEventsAsync(
      IReadOnlyList<Guid> eventIds,
      string perspectiveName,
      TimeSpan timeout,
      Guid? awaiterId = null,
      CancellationToken cancellationToken = default) {
    if (eventIds is null || eventIds.Count == 0) {
      return Task.FromResult(true);
    }

    var resolvedAwaiterId = awaiterId ?? TrackedGuid.NewMedo();
    var pendingKeys = eventIds
        .Where(id => _trackedEvents.ContainsKey((id, perspectiveName)))
        .Select(id => (id, perspectiveName))
        .ToList();

    return _waitForCompletionAsync(
        _perspectiveWaiters,
        pendingKeys,
        key => _trackedEvents.ContainsKey(key),
        resolvedAwaiterId,
        timeout,
        cancellationToken);
  }

  /// <inheritdoc />
  public Task<bool> WaitForAllPerspectivesAsync(
      IReadOnlyList<Guid> eventIds,
      TimeSpan timeout,
      Guid? awaiterId = null,
      CancellationToken cancellationToken = default) {
    if (eventIds is null || eventIds.Count == 0) {
      return Task.FromResult(true);
    }

    var resolvedAwaiterId = awaiterId ?? TrackedGuid.NewMedo();
    var pendingKeys = eventIds
        .Where(id => _trackedEvents.Keys.Any(k => k.EventId == id))
        .ToList();

    return _waitForCompletionAsync(
        _allPerspectivesWaiters,
        pendingKeys,
        key => _trackedEvents.Keys.Any(k => k.EventId == key),
        resolvedAwaiterId,
        timeout,
        cancellationToken);
  }

  /// <inheritdoc />
  public void UnregisterAwaiter(Guid awaiterId) {
    _unregisterFromDictionary(_eventWaiters, awaiterId);
    _unregisterFromDictionary(_perspectiveWaiters, awaiterId);
    _unregisterFromDictionary(_allPerspectivesWaiters, awaiterId);
  }

  /// <summary>
  /// Shared helper that registers TCS entries keyed by awaiter ID, waits for completion,
  /// and cleans up on cancellation/timeout.
  /// </summary>
  private async Task<bool> _waitForCompletionAsync<TKey>(
      ConcurrentDictionary<TKey, ConcurrentDictionary<Guid, TaskCompletionSource<bool>>> waiters,
      IReadOnlyList<TKey> pendingKeys,
      Func<TKey, bool> isPending,
      Guid awaiterId,
      TimeSpan timeout,
      CancellationToken cancellationToken) where TKey : notnull {
    if (pendingKeys.Count == 0) {
      return true;
    }

    var tasks = new List<Task<bool>>();

    foreach (var key in pendingKeys) {
      // Check again if still pending (could have been processed between checks)
      if (!isPending(key)) {
        continue;
      }

      var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

      var innerDict = waiters.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, TaskCompletionSource<bool>>());
      innerDict[awaiterId] = tcs;

      // RACE CONDITION FIX: Check AGAIN after registering the waiter.
      // If Mark*Processed ran between our first check and now,
      // the event is already removed from _trackedEvents but our TCS wasn't signaled.
      // In that case, signal it ourselves to avoid a timeout.
      if (!isPending(key)) {
        tcs.TrySetResult(true);
      }

      tasks.Add(tcs.Task);
    }

    if (tasks.Count == 0) {
      return true;
    }

    // Wait for all events with timeout
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      await Task.WhenAll(tasks).WaitAsync(cts.Token);
      return true;
    } catch (OperationCanceledException) {
      // Clean up this specific awaiter's registrations
      UnregisterAwaiter(awaiterId);
      return false;
    }
  }

  /// <summary>
  /// Removes all entries for a specific awaiter from a waiter dictionary and cancels their TCS.
  /// </summary>
  private static void _unregisterFromDictionary<TKey>(
      ConcurrentDictionary<TKey, ConcurrentDictionary<Guid, TaskCompletionSource<bool>>> waiters,
      Guid awaiterId) where TKey : notnull {
    foreach (var outerKvp in waiters) {
      if (outerKvp.Value.TryRemove(awaiterId, out var tcs)) {
        tcs.TrySetCanceled();
      }

      // Clean up empty inner dictionaries
      if (outerKvp.Value.IsEmpty) {
        waiters.TryRemove(outerKvp.Key, out _);
      }
    }
  }
}
