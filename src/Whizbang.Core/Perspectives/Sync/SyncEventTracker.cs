using System.Collections.Concurrent;

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
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync#tracker-implementation</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncEventTrackerTests.cs</tests>
public sealed class SyncEventTracker : ISyncEventTracker {
  // Key is (eventId, perspectiveName) to allow the same event to be tracked for multiple perspectives
  private readonly ConcurrentDictionary<(Guid EventId, string PerspectiveName), TrackedSyncEvent> _trackedEvents = new();

  // Waiters for event completion - key is eventId, value is list of TCS to complete when event is processed
  // Used by legacy WaitForEventsAsync and MarkProcessed
  private readonly ConcurrentDictionary<Guid, ConcurrentBag<TaskCompletionSource<bool>>> _eventWaiters = new();

  // Waiters for SPECIFIC perspective completion - key is (eventId, perspectiveName)
  // Used by WaitForPerspectiveEventsAsync, signaled by MarkProcessedByPerspective
  private readonly ConcurrentDictionary<(Guid EventId, string PerspectiveName), ConcurrentBag<TaskCompletionSource<bool>>> _perspectiveWaiters = new();

  // Waiters for ALL perspectives completion - key is eventId
  // Used by WaitForAllPerspectivesAsync, signaled by MarkProcessedByPerspective when no perspectives remain
  private readonly ConcurrentDictionary<Guid, ConcurrentBag<TaskCompletionSource<bool>>> _allPerspectivesWaiters = new();

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

      // Signal all waiters for this event
      if (_eventWaiters.TryRemove(id, out var waiters)) {
        foreach (var tcs in waiters) {
          tcs.TrySetResult(true);
        }
      }
    }
  }

  /// <inheritdoc />
  public IReadOnlyList<Guid> GetAllTrackedEventIds() {
    return _trackedEvents.Keys.Select(k => k.EventId).Distinct().ToList();
  }

  /// <inheritdoc />
  public async Task<bool> WaitForEventsAsync(
      IReadOnlyList<Guid> eventIds,
      TimeSpan timeout,
      CancellationToken cancellationToken = default) {
    if (eventIds is null || eventIds.Count == 0) {
      return true;
    }

    // Filter to only events that are still tracked
    var pendingEventIds = eventIds.Where(id =>
        _trackedEvents.Keys.Any(k => k.EventId == id)).ToList();

    if (pendingEventIds.Count == 0) {
      // All events already processed
      return true;
    }

    // Create TCS for each pending event
    var tasks = new List<Task<bool>>();
    var completionSources = new List<TaskCompletionSource<bool>>();

    foreach (var eventId in pendingEventIds) {
      // Check again if still pending (could have been processed between checks)
      if (!_trackedEvents.Keys.Any(k => k.EventId == eventId)) {
        continue;
      }

      var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      completionSources.Add(tcs);

      var waiters = _eventWaiters.GetOrAdd(eventId, _ => new ConcurrentBag<TaskCompletionSource<bool>>());
      waiters.Add(tcs);

      // RACE CONDITION FIX: Check AGAIN after registering the waiter.
      // If MarkProcessed ran between our first check and now,
      // the event is already removed from _trackedEvents but our TCS wasn't signaled.
      // In that case, signal it ourselves to avoid a timeout.
      if (!_trackedEvents.Keys.Any(k => k.EventId == eventId)) {
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
      // Timeout or cancellation - cancel all pending TCS
      foreach (var tcs in completionSources) {
        tcs.TrySetCanceled(cts.Token);
      }
      return false;
    }
  }

  /// <inheritdoc />
  public void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName) {
    foreach (var id in eventIds) {
      // Remove entry for THIS specific perspective only
      var key = (id, perspectiveName);
      _trackedEvents.TryRemove(key, out _);

      // Signal perspective-specific waiters (for IPerspectiveSyncAwaiter)
      if (_perspectiveWaiters.TryRemove(key, out var perspectiveWaiters)) {
        foreach (var tcs in perspectiveWaiters) {
          tcs.TrySetResult(true);
        }
      }

      // Check if ALL perspectives for this event are now processed
      var hasRemainingPerspectives = _trackedEvents.Keys.Any(k => k.EventId == id);

      if (!hasRemainingPerspectives) {
        // Signal all-perspectives waiters (for IEventCompletionAwaiter)
        if (_allPerspectivesWaiters.TryRemove(id, out var allWaiters)) {
          foreach (var tcs in allWaiters) {
            tcs.TrySetResult(true);
          }
        }
      }
    }
  }

  /// <inheritdoc />
  public async Task<bool> WaitForPerspectiveEventsAsync(
      IReadOnlyList<Guid> eventIds,
      string perspectiveName,
      TimeSpan timeout,
      CancellationToken cancellationToken = default) {
    if (eventIds is null || eventIds.Count == 0) {
      return true;
    }

    // Filter to only events that are still tracked for THIS perspective
    var pendingEventIds = eventIds.Where(id =>
        _trackedEvents.ContainsKey((id, perspectiveName))).ToList();

    if (pendingEventIds.Count == 0) {
      // All events already processed by this perspective
      return true;
    }

    // Create TCS for each pending (eventId, perspectiveName) pair
    var tasks = new List<Task<bool>>();
    var completionSources = new List<TaskCompletionSource<bool>>();

    foreach (var eventId in pendingEventIds) {
      var key = (eventId, perspectiveName);

      // Check again if still pending (could have been processed between checks)
      if (!_trackedEvents.ContainsKey(key)) {
        continue;
      }

      var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      completionSources.Add(tcs);

      var waiters = _perspectiveWaiters.GetOrAdd(key, _ => new ConcurrentBag<TaskCompletionSource<bool>>());
      waiters.Add(tcs);

      // RACE CONDITION FIX: Check AGAIN after registering the waiter.
      // If MarkProcessedByPerspective ran between our first check and now,
      // the event is already removed from _trackedEvents but our TCS wasn't signaled.
      // In that case, signal it ourselves to avoid a timeout.
      if (!_trackedEvents.ContainsKey(key)) {
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
      // Timeout or cancellation - cancel all pending TCS
      foreach (var tcs in completionSources) {
        tcs.TrySetCanceled(cts.Token);
      }
      return false;
    }
  }

  /// <inheritdoc />
  public async Task<bool> WaitForAllPerspectivesAsync(
      IReadOnlyList<Guid> eventIds,
      TimeSpan timeout,
      CancellationToken cancellationToken = default) {
    if (eventIds is null || eventIds.Count == 0) {
      return true;
    }

    // Filter to only events that are still tracked (by ANY perspective)
    var pendingEventIds = eventIds.Where(id =>
        _trackedEvents.Keys.Any(k => k.EventId == id)).ToList();

    if (pendingEventIds.Count == 0) {
      // All events already fully processed by all perspectives
      return true;
    }

    // Create TCS for each pending event
    var tasks = new List<Task<bool>>();
    var completionSources = new List<TaskCompletionSource<bool>>();

    foreach (var eventId in pendingEventIds) {
      // Check again if still pending (could have been processed between checks)
      if (!_trackedEvents.Keys.Any(k => k.EventId == eventId)) {
        continue;
      }

      var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      completionSources.Add(tcs);

      var waiters = _allPerspectivesWaiters.GetOrAdd(eventId, _ => new ConcurrentBag<TaskCompletionSource<bool>>());
      waiters.Add(tcs);

      // RACE CONDITION FIX: Check AGAIN after registering the waiter.
      // If MarkProcessedByPerspective ran between our first check and now,
      // the event is already removed from _trackedEvents but our TCS wasn't signaled.
      // In that case, signal it ourselves to avoid a timeout.
      if (!_trackedEvents.Keys.Any(k => k.EventId == eventId)) {
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
      // Timeout or cancellation - cancel all pending TCS
      foreach (var tcs in completionSources) {
        tcs.TrySetCanceled(cts.Token);
      }
      return false;
    }
  }
}
