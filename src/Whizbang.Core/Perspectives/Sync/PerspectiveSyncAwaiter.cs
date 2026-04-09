using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Implementation of <see cref="IPerspectiveSyncAwaiter"/> using event-driven sync.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses <see cref="ISyncEventTracker"/> for event-driven waiting.
/// Events are tracked at emit time and waiters are notified when processing completes.
/// </para>
/// <para>
/// <see cref="IsCaughtUpAsync"/> still uses database queries for one-shot status checks.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <docs>operations/observability/tracing#perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs</tests>
/// <remarks>
/// Initializes a new instance of <see cref="PerspectiveSyncAwaiter"/>.
/// </remarks>
/// <remarks>
/// <para>
/// When <paramref name="tracker"/> is provided, events are tracked within the same scope for
/// explicit event ID tracking (scope-based sync with <see cref="WaitAsync"/>).
/// </para>
/// <para>
/// The <paramref name="syncEventTracker"/> is required and enables event-driven waiting
/// for both <see cref="WaitAsync"/> and <see cref="WaitForStreamAsync"/>.
/// </para>
/// </remarks>
/// <param name="coordinator">The work coordinator for database queries.</param>
/// <param name="clock">The debugger-aware clock.</param>
/// <param name="logger">The logger for sync operations.</param>
/// <param name="syncEventTracker">The singleton event tracker for event-driven sync.</param>
/// <param name="tracker">Optional scoped event tracker for capturing emitted events.</param>
public sealed partial class PerspectiveSyncAwaiter(
    IWorkCoordinator coordinator,
    IDebuggerAwareClock clock,
    ILogger<PerspectiveSyncAwaiter> logger,
    ISyncEventTracker syncEventTracker,
    IScopedEventTracker? tracker = null,
    ILifecycleContextAccessor? lifecycleContextAccessor = null) : IPerspectiveSyncAwaiter {
  private const string TAG_SYNC_OUTCOME = "whizbang.sync.outcome";
  private const string TAG_SYNC_EVENT_COUNT = "whizbang.sync.event_count";

  /// <inheritdoc />
  public Guid AwaiterId { get; } = TrackedGuid.NewMedo();

  private readonly IScopedEventTracker? _tracker = tracker;
  private readonly ISyncEventTracker _syncEventTracker = syncEventTracker ?? throw new ArgumentNullException(nameof(syncEventTracker));
  private readonly IWorkCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
  private readonly IDebuggerAwareClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
  private readonly ILogger<PerspectiveSyncAwaiter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly ILifecycleContextAccessor? _lifecycleContextAccessor = lifecycleContextAccessor;


  /// <inheritdoc />
  public Task<bool> IsCaughtUpAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(options);

    if (_tracker is null) {
      throw new InvalidOperationException(
          "IsCaughtUpAsync requires IScopedEventTracker. Use WaitForStreamAsync for stream-based sync.");
    }

    return _isCaughtUpCoreAsync(perspectiveType, options, ct);
  }

  private async Task<bool> _isCaughtUpCoreAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct) {
    var pendingEvents = _tracker!.GetEmittedEvents(options.Filter);

    // If no events match the filter, we're caught up
    if (pendingEvents.Count == 0) {
      return true;
    }

    // Build sync inquiries from captured events
    var perspectiveName = _getPerspectiveName(perspectiveType);
    var inquiries = _buildSyncInquiries(pendingEvents, perspectiveName);

    if (inquiries.Length == 0) {
      return true;
    }

    // Query database for sync status
    var batch = await _querySyncStatusAsync(inquiries, ct);
    var results = batch.SyncInquiryResults ?? [];

    // Match results with their inquiry to set ExpectedEventIds for proper IsFullySynced evaluation
    // This prevents false positives when events haven't reached wh_perspective_events yet
    var resultsWithExpected = results.Select(r => {
      var matchingInquiry = inquiries.FirstOrDefault(i =>
        i.StreamId == r.StreamId && i.InquiryId == r.InquiryId);
      return matchingInquiry?.EventIds is { Length: > 0 }
        ? r with { ExpectedEventIds = matchingInquiry.EventIds }
        : r;
    }).ToArray();

    // Check if all inquiries are fully synced
    return resultsWithExpected.All(r => r.IsFullySynced);
  }

  /// <inheritdoc />
  public Task<SyncResult> WaitAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(options);
    _throwIfInsideInlineStage();

    if (_tracker is null) {
      throw new InvalidOperationException(
          "WaitAsync requires IScopedEventTracker. Use WaitForStreamAsync for stream-based sync.");
    }

    return _waitCoreAsync(perspectiveType, options, ct);
  }

  private async Task<SyncResult> _waitCoreAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct) {
    // Create span for perspective sync wait - shows blocking time in traces
    using var syncActivity = WhizbangActivitySource.Tracing.StartActivity(
      $"PerspectiveSync {perspectiveType.Name}",
      ActivityKind.Internal);
    syncActivity?.SetTag("whizbang.sync.perspective", perspectiveType.FullName);
    syncActivity?.SetTag("whizbang.sync.timeout_ms", options.Timeout.TotalMilliseconds);

    var stopwatch = _clock.StartNew();
    var pendingEvents = _tracker!.GetEmittedEvents(options.Filter);

    var perspectiveName = _getPerspectiveName(perspectiveType);

    // If no events match the filter, return immediately
    if (pendingEvents.Count == 0) {
      syncActivity?.SetTag(TAG_SYNC_OUTCOME, "NoPendingEvents");
      syncActivity?.SetTag(TAG_SYNC_EVENT_COUNT, 0);
      return new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.ActiveElapsed,
          EventsEmitted: 0, PerspectiveName: perspectiveName);
    }

    var eventsToWait = pendingEvents.Count;
    var inquiries = _buildSyncInquiries(pendingEvents, perspectiveName);

    if (inquiries.Length == 0) {
      syncActivity?.SetTag(TAG_SYNC_OUTCOME, "NoPendingEvents");
      syncActivity?.SetTag(TAG_SYNC_EVENT_COUNT, 0);
      return new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.ActiveElapsed,
          EventsEmitted: pendingEvents.Count, PerspectiveName: perspectiveName);
    }

    // Set event count on activity now that we know the count
    syncActivity?.SetTag(TAG_SYNC_EVENT_COUNT, eventsToWait);
    syncActivity?.SetTag("whizbang.sync.stream_count", inquiries.Length);

    // Log sync wait starting
    LogSyncWaitStarting(_logger, perspectiveName, eventsToWait, inquiries.Length);

    // DEBUG: Log the expected event IDs we're waiting for
    if (_logger.IsEnabled(LogLevel.Debug)) {
      foreach (var inquiry in inquiries) {
        var eventIdsStr = string.Join(", ", inquiry.EventIds ?? []);
        LogSyncDebugWaiting(_logger, inquiry.StreamId, inquiry.PerspectiveName ?? perspectiveName, eventIdsStr);
      }
    }

    // Event-driven waiting: use ISyncEventTracker to wait for all events to be processed
    var allEventIds = inquiries.SelectMany(i => i.EventIds ?? []).ToArray();
    var success = await _syncEventTracker.WaitForPerspectiveEventsAsync(
        allEventIds, perspectiveName, options.Timeout, AwaiterId, ct);
    stopwatch.Halt();

    if (success) {
      syncActivity?.SetTag(TAG_SYNC_OUTCOME, "Synced");
      syncActivity?.SetTag("whizbang.sync.elapsed_ms", stopwatch.ActiveElapsed.TotalMilliseconds);
      LogSyncWaitCompleted(_logger, perspectiveName, eventsToWait, stopwatch.ActiveElapsed.TotalMilliseconds);
      return new SyncResult(SyncOutcome.Synced, eventsToWait, stopwatch.ActiveElapsed,
          EventsEmitted: pendingEvents.Count, EventsTracked: eventsToWait, PerspectiveName: perspectiveName);
    }

    syncActivity?.SetTag(TAG_SYNC_OUTCOME, "TimedOut");
    syncActivity?.SetTag("whizbang.sync.elapsed_ms", stopwatch.ActiveElapsed.TotalMilliseconds);
    LogSyncWaitTimedOut(_logger, perspectiveName, eventsToWait, stopwatch.ActiveElapsed.TotalMilliseconds);
    return new SyncResult(SyncOutcome.TimedOut, eventsToWait, stopwatch.ActiveElapsed,
        EventsEmitted: pendingEvents.Count, EventsTracked: eventsToWait, PerspectiveName: perspectiveName);
  }

  /// <inheritdoc />
  public Task<SyncResult> WaitForStreamAsync(
      Type perspectiveType,
      Guid streamId,
      Type[]? eventTypes,
      TimeSpan timeout,
      Guid? eventIdToAwait = null,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    _throwIfInsideInlineStage();
    return _waitForStreamCoreAsync(perspectiveType, streamId, eventTypes, timeout, eventIdToAwait, ct);
  }

  private async Task<SyncResult> _waitForStreamCoreAsync(
      Type perspectiveType,
      Guid streamId,
      Type[]? eventTypes,
      TimeSpan timeout,
      Guid? eventIdToAwait,
      CancellationToken ct) {
    using var syncActivity = WhizbangActivitySource.Tracing.StartActivity(
      $"PerspectiveSync {perspectiveType.Name} Stream",
      ActivityKind.Internal);
    _setStreamSyncActivityTags(syncActivity, perspectiveType, streamId, timeout, eventIdToAwait);

    var stopwatch = _clock.StartNew();
    var perspectiveName = _getPerspectiveName(perspectiveType);
    var expectedEventIds = _resolveExpectedEventIds(eventIdToAwait, streamId, perspectiveName, eventTypes);

    LogStreamSyncWaitStarting(_logger, perspectiveName, streamId);

    // EVENT-DRIVEN WAITING: If we have expected event IDs, use the tracker
    if (expectedEventIds is { Length: > 0 }) {
      return await _waitForExpectedEventsAsync(
        expectedEventIds, perspectiveName, streamId, timeout, stopwatch, syncActivity, ct);
    }

    // No event IDs to wait for
    stopwatch.Halt();
    _setSyncActivityOutcome(syncActivity, "NoPendingEvents", 0, stopwatch.ActiveElapsed);
    LogSyncDebugNoEventsFound(_logger, streamId);
    return new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.ActiveElapsed,
        EventsTracked: 0, PerspectiveName: perspectiveName);
  }

  private static void _setStreamSyncActivityTags(
      Activity? syncActivity, Type perspectiveType, Guid streamId, TimeSpan timeout, Guid? eventIdToAwait) {
    syncActivity?.SetTag("whizbang.sync.perspective", perspectiveType.FullName);
    syncActivity?.SetTag("whizbang.sync.stream_id", streamId.ToString());
    syncActivity?.SetTag("whizbang.sync.timeout_ms", timeout.TotalMilliseconds);
    if (eventIdToAwait.HasValue) {
      syncActivity?.SetTag("whizbang.sync.event_id", eventIdToAwait.Value.ToString());
    }
  }

  private Guid[]? _resolveExpectedEventIds(
      Guid? eventIdToAwait, Guid streamId, string perspectiveName, Type[]? eventTypes) {
    // Priority 1: Use explicit event ID if provided
    if (eventIdToAwait.HasValue) {
#pragma warning disable CA1848
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: Using explicit eventIdToAwait={EventId}", eventIdToAwait.Value);
      }
#pragma warning restore CA1848
      return [eventIdToAwait.Value];
    }

    // Priority 2: Use singleton ISyncEventTracker for cross-scope sync
    var trackedSyncEvents = _syncEventTracker.GetPendingEvents(streamId, perspectiveName, eventTypes);
#pragma warning disable CA1848
    if (_logger.IsEnabled(LogLevel.Debug)) {
      var eventTypeNames = eventTypes?.Select(t => t.Name).ToArray() ?? [];
      _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: Queried singleton tracker - StreamId={StreamId}, Perspective={Perspective}, EventTypes=[{Types}], FoundCount={Count}",
        streamId, perspectiveName, string.Join(", ", eventTypeNames), trackedSyncEvents.Count);
      if (trackedSyncEvents.Count > 0) {
        _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: Tracked events - [{Events}]",
          string.Join(", ", trackedSyncEvents.Select(e => $"{e.EventType.Name}:{e.EventId}")));
      }
    }
#pragma warning restore CA1848
    return trackedSyncEvents.Count > 0
      ? [.. trackedSyncEvents.Select(e => e.EventId)]
      : null;
  }

  private async Task<SyncResult> _waitForExpectedEventsAsync(
      Guid[] expectedEventIds, string perspectiveName, Guid streamId,
      TimeSpan timeout, IActiveStopwatch stopwatch, Activity? syncActivity, CancellationToken ct) {
#pragma warning disable CA1848
    if (_logger.IsEnabled(LogLevel.Debug)) {
      _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: Starting event-driven wait for {Count} events - [{Ids}]",
        expectedEventIds.Length, string.Join(", ", expectedEventIds));
    }
#pragma warning restore CA1848
    var success = await _syncEventTracker.WaitForPerspectiveEventsAsync(expectedEventIds, perspectiveName, timeout, AwaiterId, ct);
    stopwatch.Halt();

    if (success) {
      _setSyncActivityOutcome(syncActivity, "Synced", expectedEventIds.Length, stopwatch.ActiveElapsed);
      LogStreamSyncWaitCompleted(_logger, perspectiveName, streamId, expectedEventIds.Length, stopwatch.ActiveElapsed.TotalMilliseconds);
      return new SyncResult(SyncOutcome.Synced, expectedEventIds.Length, stopwatch.ActiveElapsed,
          EventsTracked: expectedEventIds.Length, PerspectiveName: perspectiveName);
    }

#pragma warning disable CA1848
    if (_logger.IsEnabled(LogLevel.Debug)) {
      _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: Event-driven wait TIMED OUT after {Ms}ms waiting for [{Ids}]",
        stopwatch.ActiveElapsed.TotalMilliseconds, string.Join(", ", expectedEventIds));
    }
#pragma warning restore CA1848
    _setSyncActivityOutcome(syncActivity, "TimedOut", expectedEventIds.Length, stopwatch.ActiveElapsed);
    LogStreamSyncWaitTimedOut(_logger, perspectiveName, streamId, stopwatch.ActiveElapsed.TotalMilliseconds);
    return new SyncResult(SyncOutcome.TimedOut, expectedEventIds.Length, stopwatch.ActiveElapsed,
        EventsTracked: expectedEventIds.Length, PerspectiveName: perspectiveName);
  }

  private static void _setSyncActivityOutcome(Activity? syncActivity, string outcome, int eventCount, TimeSpan elapsed) {
    syncActivity?.SetTag(TAG_SYNC_OUTCOME, outcome);
    syncActivity?.SetTag(TAG_SYNC_EVENT_COUNT, eventCount);
    syncActivity?.SetTag("whizbang.sync.elapsed_ms", elapsed.TotalMilliseconds);
  }

  /// <summary>
  /// Builds sync inquiries from tracked events, grouped by stream.
  /// </summary>
  private static SyncInquiry[] _buildSyncInquiries(
      IReadOnlyList<TrackedEvent> events,
      string perspectiveName) {
    return [.. events
      .GroupBy(e => e.StreamId)
      .Select(g => new SyncInquiry {
        StreamId = g.Key,
        PerspectiveName = perspectiveName,
        EventIds = [.. g.Select(e => e.EventId)],
        IncludeProcessedEventIds = true // Request processed IDs for explicit comparison
      })];
  }

  /// <summary>
  /// Queries the database for sync status using the batch function.
  /// </summary>
  private async Task<WorkBatch> _querySyncStatusAsync(
      SyncInquiry[] inquiries,
      CancellationToken ct) {
    // Create minimal request just for sync queries
    var request = new ProcessWorkBatchRequest {
      // Use placeholder values for sync-only queries
      // The batch function will process sync inquiries regardless of instance info
      InstanceId = Guid.Empty,
      ServiceName = "SyncQuery",
      HostName = "local",
      ProcessId = 0,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      PerspectiveSyncInquiries = inquiries,
      Flags = WorkBatchOptions.None // Sync queries are processed regardless of flags
    };

    return await _coordinator.ProcessWorkBatchAsync(request, ct);
  }

  /// <summary>
  /// Gets the perspective name from the perspective type.
  /// Uses CLR type name format to match database storage and source generator output.
  /// </summary>
  private static string _getPerspectiveName(Type perspectiveType) {
    // Use Type.FullName directly - CLR format with '+' for nested types
    // This matches TypeNameUtilities.BuildClrTypeName() used in generators
    // and the format stored in wh_perspective_events
    return perspectiveType.FullName ?? perspectiveType.Name;
  }

  // ==========================================================================
  // LoggerMessage definitions
  // ==========================================================================

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "Sync wait starting: Perspective={PerspectiveName}, events={EventCount}, streams={StreamCount}"
  )]
  private static partial void LogSyncWaitStarting(ILogger logger, string perspectiveName, int eventCount, int streamCount);

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Information,
    Message = "Sync wait completed: Perspective={PerspectiveName}, events={EventCount}, elapsed={ElapsedMs:F1}ms"
  )]
  private static partial void LogSyncWaitCompleted(ILogger logger, string perspectiveName, int eventCount, double elapsedMs);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Warning,
    Message = "Sync wait timed out: Perspective={PerspectiveName}, events={EventCount}, elapsed={ElapsedMs:F1}ms"
  )]
  private static partial void LogSyncWaitTimedOut(ILogger logger, string perspectiveName, int eventCount, double elapsedMs);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Information,
    Message = "Stream sync wait starting: Perspective={PerspectiveName}, StreamId={StreamId}"
  )]
  private static partial void LogStreamSyncWaitStarting(ILogger logger, string perspectiveName, Guid streamId);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Information,
    Message = "Stream sync wait completed: Perspective={PerspectiveName}, StreamId={StreamId}, processed={ProcessedCount}, elapsed={ElapsedMs:F1}ms"
  )]
  private static partial void LogStreamSyncWaitCompleted(ILogger logger, string perspectiveName, Guid streamId, int processedCount, double elapsedMs);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Warning,
    Message = "Stream sync wait timed out: Perspective={PerspectiveName}, StreamId={StreamId}, elapsed={ElapsedMs:F1}ms"
  )]
  private static partial void LogStreamSyncWaitTimedOut(ILogger logger, string perspectiveName, Guid streamId, double elapsedMs);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Debug,
    Message = "[SYNC_DEBUG] WaitAsync: Waiting for StreamId={StreamId}, Perspective={PerspectiveName}, EventIds=[{EventIds}]"
  )]
  private static partial void LogSyncDebugWaiting(ILogger logger, Guid streamId, string perspectiveName, string eventIds);

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Debug,
    Message = "[SYNC_DEBUG] WaitForStreamAsync: No events tracked for stream={StreamId}. Returning NoPendingEvents."
  )]
  private static partial void LogSyncDebugNoEventsFound(ILogger logger, Guid streamId);

  /// <summary>
  /// Throws if called from inside an Inline lifecycle stage.
  /// Calling WaitForStreamAsync/WaitAsync from an Inline stage deadlocks the work coordinator
  /// because the single-threaded coordinator cannot process perspective commits while blocked.
  /// Detached stages are safe because they run in their own scope on the thread pool.
  /// </summary>
  private void _throwIfInsideInlineStage() {
    if (_lifecycleContextAccessor?.Current is { } ctx && !ctx.CurrentStage.IsDetached()) {
      throw new InvalidOperationException(
        $"WaitForStreamAsync/WaitAsync cannot be called inside an Inline lifecycle " +
        $"receptor (current stage: {ctx.CurrentStage}). This would deadlock the work " +
        $"coordinator. Use a Detached stage or event enrichment instead.");
    }
  }
}
