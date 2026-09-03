using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Decorator for <see cref="IEventStore"/> that implements <see cref="IEventStore.AppendAndWaitAsync{TMessage,TPerspective}"/>
/// by appending events and waiting for perspective synchronization.
/// </summary>
/// <remarks>
/// <para>
/// This decorator provides the synchronous verification pattern for request-response
/// over event-sourced aggregates. After appending an event, it waits for the specified
/// perspective to process the event before returning.
/// </para>
/// <para>
/// Register this decorator in DI to enable append-and-wait functionality:
/// <code>
/// services.Decorate&lt;IEventStore, AppendAndWaitEventStoreDecorator&gt;();
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-store#append-and-wait</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/AppendAndWaitEventStoreDecoratorTests.cs</tests>
/// <remarks>
/// Initializes a new instance of <see cref="AppendAndWaitEventStoreDecorator"/>.
/// </remarks>
/// <param name="inner">The underlying event store implementation.</param>
/// <param name="syncAwaiter">The perspective sync awaiter for waiting on perspective processing.</param>
/// <param name="eventCompletionAwaiter">Optional event completion awaiter for waiting on all perspectives.</param>
/// <param name="scopedEventTracker">Optional scoped event tracker for tracking emitted events.</param>
public sealed class AppendAndWaitEventStoreDecorator(
    IEventStore inner,
    IPerspectiveSyncAwaiter syncAwaiter,
    IEventCompletionAwaiter? eventCompletionAwaiter = null,
    IScopedEventTracker? scopedEventTracker = null)
    // IEventStore is re-listed so the AppendAndWaitAsync implementations below re-map onto the
    // interface — without it the base class's interface map wins and callers would get the
    // interface's no-wait default instead.
    : ForwardingEventStoreDecorator(inner), IEventStore {
  private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(30);

  private readonly IPerspectiveSyncAwaiter _syncAwaiter = syncAwaiter ?? throw new ArgumentNullException(nameof(syncAwaiter));
  private readonly IEventCompletionAwaiter? _eventCompletionAwaiter = eventCompletionAwaiter;
  private readonly IScopedEventTracker? _scopedEventTracker = scopedEventTracker;

  /// <inheritdoc />
  public async Task<SyncResult> AppendAndWaitAsync<TMessage, TPerspective>(
      Guid streamId,
      TMessage message,
      TimeSpan? timeout = null,
      Action<SyncWaitingContext>? onWaiting = null,
      Action<SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull
      where TPerspective : class {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var startedAt = DateTimeOffset.UtcNow;
    var perspectiveType = typeof(TPerspective);
    var effectiveTimeout = timeout ?? _defaultTimeout;

    // Append the event to the store
    await Inner.AppendAsync(streamId, message, cancellationToken);

    // Invoke onWaiting before starting the wait
    _invokeOnWaiting(onWaiting, perspectiveType, eventCount: 1, [streamId], effectiveTimeout, startedAt);

    // Wait for the perspective to process the event
    var result = await _syncAwaiter.WaitForStreamAsync(
        perspectiveType,
        streamId,
        eventTypes: null,
        timeout: effectiveTimeout,
        eventIdToAwait: null,
        ct: cancellationToken);

    stopwatch.Stop();
    var finalResult = new SyncResult(result.Outcome, result.EventsAwaited, stopwatch.Elapsed);
    _invokeOnDecisionMade(onDecisionMade, perspectiveType, finalResult, didWait: true);
    return finalResult;
  }

  /// <inheritdoc />
  public async Task<SyncResult> AppendAndWaitAsync<TMessage>(
      Guid streamId,
      TMessage message,
      TimeSpan? timeout = null,
      Action<SyncWaitingContext>? onWaiting = null,
      Action<SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var startedAt = DateTimeOffset.UtcNow;
    var effectiveTimeout = timeout ?? _defaultTimeout;

    // Append the event to the store
    await Inner.AppendAsync(streamId, message, cancellationToken);

    // Get tracked events from scoped tracker
    var scopedTracker = _scopedEventTracker ?? ScopedEventTrackerAccessor.CurrentTracker;
    if (scopedTracker is null || _eventCompletionAwaiter is null) {
      // No tracker or awaiter - return synced (can't verify either way)
      var syncedResult = new SyncResult(SyncOutcome.Synced, 1, stopwatch.Elapsed);
      _invokeOnDecisionMade(onDecisionMade, perspectiveType: null, syncedResult, didWait: false);
      return syncedResult;
    }

    var trackedEvents = scopedTracker.GetEmittedEvents();
    if (trackedEvents.Count == 0) {
      // No events tracked - return NoPendingEvents
      var noPendingResult = new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.Elapsed);
      _invokeOnDecisionMade(onDecisionMade, perspectiveType: null, noPendingResult, didWait: false);
      return noPendingResult;
    }

    var eventIds = trackedEvents.Select(e => e.EventId).ToList();
    var streamIds = trackedEvents.Select(e => e.StreamId).Distinct().ToList();

    // Invoke onWaiting before starting the wait
    _invokeOnWaiting(onWaiting, perspectiveType: null, eventIds.Count, streamIds, effectiveTimeout, startedAt);

    // Wait for all perspectives to process the events
    var completed = await _eventCompletionAwaiter.WaitForEventsAsync(eventIds, effectiveTimeout, cancellationToken);

    stopwatch.Stop();
    var result = new SyncResult(
        completed ? SyncOutcome.Synced : SyncOutcome.TimedOut,
        eventIds.Count,
        stopwatch.Elapsed);

    _invokeOnDecisionMade(onDecisionMade, perspectiveType: null, result, didWait: true);
    return result;
  }

  /// <summary>
  /// Invokes the onWaiting callback safely, swallowing any exceptions.
  /// </summary>
  private static void _invokeOnWaiting(
      Action<SyncWaitingContext>? onWaiting,
      Type? perspectiveType,
      int eventCount,
      IReadOnlyList<Guid> streamIds,
      TimeSpan timeout,
      DateTimeOffset startedAt) {
    if (onWaiting is null) {
      return;
    }

    try {
      var context = new SyncWaitingContext {
        PerspectiveType = perspectiveType,
        EventCount = eventCount,
        StreamIds = streamIds,
        Timeout = timeout,
        StartedAt = startedAt
      };
      onWaiting(context);
    } catch {
      // Swallow exceptions - one bad callback shouldn't break sync
    }
  }

  /// <summary>
  /// Invokes the onDecisionMade callback safely, swallowing any exceptions.
  /// </summary>
  private static void _invokeOnDecisionMade(
      Action<SyncDecisionContext>? onDecisionMade,
      Type? perspectiveType,
      SyncResult result,
      bool didWait) {
    if (onDecisionMade is null) {
      return;
    }

    try {
      var context = new SyncDecisionContext {
        PerspectiveType = perspectiveType,
        Outcome = result.Outcome,
        EventsAwaited = result.EventsAwaited,
        ElapsedTime = result.ElapsedTime,
        DidWait = didWait
      };
      onDecisionMade(context);
    } catch {
      // Swallow exceptions - one bad callback shouldn't break sync
    }
  }

}
