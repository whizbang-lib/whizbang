// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Thread-safe singleton implementation of event tracking for perspective sync.
/// </summary>
/// <remarks>
/// <para>
/// Tracked entries are indexed by event id, then perspective, under one short lock: whether an event is
/// still pending on any perspective is a dictionary lookup, never a scan of every tracked key (#2228).
/// Waiters are signaled outside the lock, always after the entry they wait on was removed.
/// </para>
/// <para>
/// An entry leaves when this instance applies the event. When the perspective runs on another instance,
/// or the event fails or is dead-lettered, nothing here applies it: <see cref="CleanupStaleEntries"/>,
/// run by the host-wide in-process tracker sweep, is then the entry's end. Ages are read from the
/// injected <see cref="TimeProvider"/>.
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
/// canceled — without affecting other awaiters waiting on the same events.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#tracker-implementation</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/Sync/SyncEventTrackerTests.cs</tests>
public sealed class SyncEventTracker : ISyncEventTracker {
  // Event id -> perspective name -> entry, so the same event is tracked once per perspective. Guarded by _gate.
  private readonly Dictionary<Guid, Dictionary<string, TrackedSyncEvent>> _trackedEvents = [];
  private readonly Lock _gate = new();
  private readonly TimeProvider _timeProvider;

  /// <summary>Initializes a new instance of the <see cref="SyncEventTracker"/> class.</summary>
  /// <param name="timeProvider">The clock entries are aged by; the system clock when omitted.</param>
  /// <docs>fundamentals/perspectives/perspective-sync#tracker-implementation</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncEventTrackerStaleSweepTests.cs:CleanupStaleEntries_RemovesAnEntryOnlyOnceTheInjectedClockPassesItsAgeAsync</tests>
  public SyncEventTracker(TimeProvider? timeProvider = null) {
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

  /// <summary>The number of (event, perspective) entries tracked right now.</summary>
  internal int TrackedEntryCount {
    get {
      lock (_gate) {
        return _trackedEvents.Values.Sum(perspectives => perspectives.Count);
      }
    }
  }

  // Waiters keyed by (outerKey, awaiterId) → TCS.
  // Inner ConcurrentDictionary is keyed by AwaiterId for O(1) removal on cancellation.

  // Used by WaitForEventsAsync / MarkProcessed — outer key is eventId
  private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, TaskCompletionSource<bool>>> _eventWaiters = new();

  // Used by WaitForPerspectiveEventsAsync / MarkProcessedByPerspective — outer key is (eventId, perspectiveName)
  private readonly ConcurrentDictionary<(Guid EventId, string PerspectiveName), ConcurrentDictionary<Guid, TaskCompletionSource<bool>>> _perspectiveWaiters = new();

  // Used by WaitForAllPerspectivesAsync / MarkProcessedByPerspective — outer key is eventId
  private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, TaskCompletionSource<bool>>> _allPerspectivesWaiters = new();

  /// <summary>How many recently applied (event, perspective) pairs <see cref="MarkApplied"/> remembers.</summary>
  internal const int RECENTLY_APPLIED_CAPACITY = 16_384;

  // (eventId, perspectiveName) pairs recently marked applied, so a wait that starts just after a local apply
  // returns at once. Bounded: the queue holds insertion order and the oldest pair is forgotten first.
  private readonly ConcurrentDictionary<(Guid EventId, string PerspectiveName), byte> _recentlyApplied = new();
  private readonly ConcurrentQueue<(Guid EventId, string PerspectiveName)> _recentlyAppliedOrder = new();

  // Used by WhenAppliedAsync / MarkApplied — outer key is (eventId, perspectiveName), inner key a waiter id.
  private readonly ConcurrentDictionary<(Guid EventId, string PerspectiveName), ConcurrentDictionary<Guid, TaskCompletionSource>> _appliedWaiters = new();

  /// <summary>The number of recently applied pairs remembered (for tests).</summary>
  internal int RecentlyAppliedCount => _recentlyApplied.Count;

  /// <summary>The number of registered applied waiters (for tests).</summary>
  internal int AppliedWaiterCount => _appliedWaiters.Values.Sum(w => w.Count);

  /// <inheritdoc />
  public void MarkApplied(IEnumerable<Guid> eventIds, string perspectiveName) {
    ArgumentNullException.ThrowIfNull(eventIds);
    ArgumentNullException.ThrowIfNull(perspectiveName);

    foreach (var eventId in eventIds) {
      var key = (eventId, perspectiveName);
      // Remembered BEFORE waiters are released, so a waiter registering concurrently either finds its
      // entry here on its re-check or is in the dictionary drained below.
      if (_recentlyApplied.TryAdd(key, 0)) {
        _recentlyAppliedOrder.Enqueue(key);
        while (_recentlyAppliedOrder.Count > RECENTLY_APPLIED_CAPACITY && _recentlyAppliedOrder.TryDequeue(out var oldest)) {
          _recentlyApplied.TryRemove(oldest, out _);
        }
      }
      if (_appliedWaiters.TryRemove(key, out var waiters)) {
        foreach (var waiter in waiters.Values) {
          waiter.TrySetResult();
        }
      }
    }
  }

  /// <inheritdoc />
  public Task WhenAppliedAsync(Guid eventId, string perspectiveName, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(perspectiveName);
    if (cancellationToken.IsCancellationRequested) {
      return Task.FromCanceled(cancellationToken);
    }

    var key = (eventId, perspectiveName);
    var waiterId = TrackedGuid.New().Value;
    var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var waiters = _appliedWaiters.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, TaskCompletionSource>());
    waiters[waiterId] = waiter;

    // Checked only AFTER registering. MarkApplied remembers the pair before it drains the waiters, so an
    // apply marked before this line is found here, and one marked after it finds the waiter: none is lost.
    if (_recentlyApplied.ContainsKey(key)) {
      _removeAppliedWaiter(key, waiters, waiterId);
      return Task.CompletedTask;
    }

    var registration = cancellationToken.Register(() => {
      _removeAppliedWaiter(key, waiters, waiterId);
      waiter.TrySetCanceled(cancellationToken);
    });
    _ = waiter.Task.ContinueWith(
      static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
      registration,
      CancellationToken.None,
      TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);
    return waiter.Task;
  }

  private void _removeAppliedWaiter(
      (Guid EventId, string PerspectiveName) key,
      ConcurrentDictionary<Guid, TaskCompletionSource> waiters,
      Guid waiterId) {
    waiters.TryRemove(waiterId, out _);
    if (waiters.IsEmpty) {
      _appliedWaiters.TryRemove(new KeyValuePair<(Guid, string), ConcurrentDictionary<Guid, TaskCompletionSource>>(key, waiters));
    }
  }

  /// <inheritdoc />
  public void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName) {
    var tracked = new TrackedSyncEvent(eventType, eventId, streamId, perspectiveName, _timeProvider.GetUtcNow().UtcDateTime);
    lock (_gate) {
      if (!_trackedEvents.TryGetValue(eventId, out var perspectives)) {
        perspectives = new Dictionary<string, TrackedSyncEvent>(StringComparer.Ordinal);
        _trackedEvents.Add(eventId, perspectives);
      }
      // The first track keeps its age: a repeated track never refreshes it.
      perspectives.TryAdd(perspectiveName, tracked);
    }
  }

  /// <summary>Whether <paramref name="eventId"/> is still pending on any perspective.</summary>
  private bool _isEventPending(Guid eventId) {
    lock (_gate) {
      return _trackedEvents.ContainsKey(eventId);
    }
  }

  /// <summary>Whether <paramref name="eventId"/> is still pending on <paramref name="perspectiveName"/>.</summary>
  private bool _isPending(Guid eventId, string perspectiveName) {
    lock (_gate) {
      return _trackedEvents.TryGetValue(eventId, out var perspectives) && perspectives.ContainsKey(perspectiveName);
    }
  }

  /// <summary>
  /// Removes one (event, perspective) entry, and the event's index entry with its last perspective.
  /// Must be called under <c>_gate</c>.
  /// </summary>
  private void _removeLocked(Guid eventId, string perspectiveName) {
    if (_trackedEvents.TryGetValue(eventId, out var perspectives)
        && perspectives.Remove(perspectiveName) && perspectives.Count == 0) {
      _trackedEvents.Remove(eventId);
    }
  }

  /// <inheritdoc />
  public IReadOnlyList<TrackedSyncEvent> GetPendingEvents(
      Guid streamId,
      string perspectiveName,
      Type[]? eventTypes = null) {
    var typeSet = eventTypes is { Length: > 0 } ? eventTypes.ToHashSet() : null;
    lock (_gate) {
      return [.. _trackedEvents.Values
        .Select(perspectives => perspectives.GetValueOrDefault(perspectiveName))
        .OfType<TrackedSyncEvent>()
        .Where(e => e.StreamId == streamId && typeSet?.Contains(e.EventType) != false)];
    }
  }

  /// <inheritdoc />
  public void MarkProcessed(IEnumerable<Guid> eventIds) {
    foreach (var id in eventIds) {
      // Remove entries for ALL perspectives that have this eventId
      lock (_gate) {
        _trackedEvents.Remove(id);
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
    lock (_gate) {
      return [.. _trackedEvents.Keys];
    }
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

    var resolvedAwaiterId = awaiterId ?? TrackedGuid.New();
    var pendingKeys = eventIds.Where(_isEventPending).ToList();

    return WaitForCompletionAsync(
        _eventWaiters,
        pendingKeys,
        _isEventPending,
        resolvedAwaiterId,
        timeout,
        cancellationToken);
  }

  /// <inheritdoc />
  public void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName) {
    foreach (var id in eventIds) {
      // Remove entry for THIS specific perspective only
      var key = (id, perspectiveName);
      bool hasRemainingPerspectives;
      lock (_gate) {
        _removeLocked(id, perspectiveName);
        hasRemainingPerspectives = _trackedEvents.ContainsKey(id);
      }

      // Signal perspective-specific waiters
      if (_perspectiveWaiters.TryRemove(key, out var perspectiveWaiters)) {
        foreach (var kvp in perspectiveWaiters) {
          kvp.Value.TrySetResult(true);
        }
      }

      // Signal the all-perspectives waiters once no perspective for this event is pending
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

    var resolvedAwaiterId = awaiterId ?? TrackedGuid.New();
    var pendingKeys = eventIds
        .Where(id => _isPending(id, perspectiveName))
        .Select(id => (id, perspectiveName))
        .ToList();

    return WaitForCompletionAsync(
        _perspectiveWaiters,
        pendingKeys,
        key => _isPending(key.Item1, key.Item2),
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

    var resolvedAwaiterId = awaiterId ?? TrackedGuid.New();
    var pendingKeys = eventIds.Where(_isEventPending).ToList();

    return WaitForCompletionAsync(
        _allPerspectivesWaiters,
        pendingKeys,
        _isEventPending,
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
  /// <remarks>
  /// Internal rather than private so the post-registration re-check can be driven with a predicate
  /// that changes its answer between the two calls, which is the race it exists for.
  /// </remarks>
  internal async Task<bool> WaitForCompletionAsync<TKey>(
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

  /// <inheritdoc />
  public void MarkPerspectiveStreamProcessed(string perspectiveName, Guid streamId) {
    ArgumentNullException.ThrowIfNull(perspectiveName);

    // Snapshot the matching event IDs and delegate to MarkProcessedByPerspective so the
    // single signaling path handles tracker removal + per-perspective TCS resolution +
    // all-perspectives bookkeeping consistently.
    var eventIds = GetPendingEvents(streamId, perspectiveName).Select(e => e.EventId).ToList();
    if (eventIds.Count > 0) {
      MarkProcessedByPerspective(eventIds, perspectiveName);
    }
  }

  /// <inheritdoc />
  public int CleanupStaleEntries(TimeSpan maxAge) {
    var cutoff = _timeProvider.GetUtcNow().UtcDateTime - maxAge;

    // Removed under the lock, signaled after it: a waiter's re-check never sees an entry its signal already passed.
    var removed = new List<((Guid EventId, string PerspectiveName) Key, bool EventDone)>();
    lock (_gate) {
      var stale = _trackedEvents.Values
          .SelectMany(perspectives => perspectives.Values)
          .Where(e => e.TrackedAt <= cutoff)
          .ToList();
      foreach (var entry in stale) {
        _removeLocked(entry.EventId, entry.PerspectiveName);
        removed.Add(((entry.EventId, entry.PerspectiveName), !_trackedEvents.ContainsKey(entry.EventId)));
      }
    }

    foreach (var (key, eventDone) in removed) {
      _signalPerspectiveSpecificWaiters(key);
      if (eventDone) {
        _signalAllPerspectivesWaiters(key.EventId);
      }
      _signalEventSpecificWaiters(key.EventId);
    }

    return removed.Count;
  }

  /// <summary>Removes and signals all per-perspective waiters for the given event+perspective key.</summary>
  private void _signalPerspectiveSpecificWaiters((Guid EventId, string PerspectiveName) key) {
    if (!_perspectiveWaiters.TryRemove(key, out var perspectiveWaiters)) {
      return;
    }
    foreach (var kvp in perspectiveWaiters) {
      kvp.Value.TrySetResult(true);
    }
  }

  /// <summary>Signals the "all perspectives for this event are done" waiters; called once none is still pending.</summary>
  private void _signalAllPerspectivesWaiters(Guid eventId) {
    if (!_allPerspectivesWaiters.TryRemove(eventId, out var allWaiters)) {
      return;
    }
    foreach (var kvp in allWaiters) {
      kvp.Value.TrySetResult(true);
    }
  }

  /// <summary>Removes and signals all event-id waiters regardless of perspective.</summary>
  private void _signalEventSpecificWaiters(Guid eventId) {
    if (!_eventWaiters.TryRemove(eventId, out var eventWaiters)) {
      return;
    }
    foreach (var kvp in eventWaiters) {
      kvp.Value.TrySetResult(true);
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
