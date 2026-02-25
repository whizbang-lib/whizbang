using Microsoft.Extensions.Logging;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Implementation of <see cref="IPerspectiveSyncAwaiter"/> using database-based sync.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses the scoped event tracker to capture WHAT events to check,
/// and queries the database via the batch function to check IF events are processed.
/// </para>
/// <para>
/// <strong>Why database-based sync:</strong>
/// Events flow through: Outbox → Database → Worker assignment → Perspective processing → Database update.
/// In-memory tracking cannot tell us when processing is complete - only the database knows.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs</tests>
public sealed partial class PerspectiveSyncAwaiter : IPerspectiveSyncAwaiter {
  private readonly IScopedEventTracker? _tracker;
  private readonly ISyncEventTracker? _syncEventTracker;
  private readonly IWorkCoordinator _coordinator;
  private readonly IDebuggerAwareClock _clock;
  private readonly ILogger<PerspectiveSyncAwaiter> _logger;

  // Default poll interval for sync queries
  private static readonly TimeSpan _defaultPollInterval = TimeSpan.FromMilliseconds(100);

  /// <summary>
  /// Initializes a new instance of <see cref="PerspectiveSyncAwaiter"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// When <paramref name="tracker"/> is provided, events are tracked within the same scope for
  /// explicit event ID tracking (scope-based sync with <see cref="WaitAsync"/>).
  /// </para>
  /// <para>
  /// When <paramref name="syncEventTracker"/> is provided, events can be discovered across request
  /// scopes using the singleton tracker (cross-scope sync via <see cref="WaitForStreamAsync"/>).
  /// </para>
  /// <para>
  /// When neither tracker is available, the awaiter falls back to database-based discovery
  /// (useful for stream-based sync).
  /// </para>
  /// </remarks>
  /// <param name="coordinator">The work coordinator for database queries.</param>
  /// <param name="clock">The debugger-aware clock.</param>
  /// <param name="logger">The logger for sync operations.</param>
  /// <param name="tracker">Optional scoped event tracker for capturing emitted events.</param>
  /// <param name="syncEventTracker">Optional singleton event tracker for cross-scope sync.</param>
  public PerspectiveSyncAwaiter(
      IWorkCoordinator coordinator,
      IDebuggerAwareClock clock,
      ILogger<PerspectiveSyncAwaiter> logger,
      IScopedEventTracker? tracker = null,
      ISyncEventTracker? syncEventTracker = null) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _tracker = tracker;
    _syncEventTracker = syncEventTracker;
  }


  /// <inheritdoc />
  public async Task<bool> IsCaughtUpAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(options);

    if (_tracker is null) {
      throw new InvalidOperationException(
          "IsCaughtUpAsync requires IScopedEventTracker. Use WaitForStreamAsync for stream-based sync.");
    }

    var pendingEvents = _tracker.GetEmittedEvents(options.Filter);

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
  public async Task<SyncResult> WaitAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(options);

    if (_tracker is null) {
      throw new InvalidOperationException(
          "WaitAsync requires IScopedEventTracker. Use WaitForStreamAsync for stream-based sync.");
    }

    var stopwatch = _clock.StartNew();
    var pendingEvents = _tracker.GetEmittedEvents(options.Filter);

    // If no events match the filter, return immediately
    if (pendingEvents.Count == 0) {
      return new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.ActiveElapsed);
    }

    var eventsToWait = pendingEvents.Count;
    var perspectiveName = _getPerspectiveName(perspectiveType);
    var inquiries = _buildSyncInquiries(pendingEvents, perspectiveName);

    if (inquiries.Length == 0) {
      return new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.ActiveElapsed);
    }

    // Log sync wait starting
    LogSyncWaitStarting(_logger, perspectiveName, eventsToWait, inquiries.Length);

    // DEBUG: Log the expected event IDs we're waiting for
    if (_logger.IsEnabled(LogLevel.Debug)) {
      foreach (var inquiry in inquiries) {
        var eventIdsStr = string.Join(", ", inquiry.EventIds ?? []);
        LogSyncDebugWaiting(_logger, inquiry.StreamId, inquiry.PerspectiveName ?? perspectiveName, eventIdsStr);
      }
    }

    // Poll database until synced or timeout
    var pollInterval = _defaultPollInterval;

    while (!ct.IsCancellationRequested) {
      // Check timeout
      if (options.DebuggerAwareTimeout) {
        if (stopwatch.HasTimedOut(options.Timeout)) {
          stopwatch.Halt();
          LogSyncWaitTimedOut(_logger, perspectiveName, eventsToWait, stopwatch.ActiveElapsed.TotalMilliseconds);
          return new SyncResult(SyncOutcome.TimedOut, eventsToWait, stopwatch.ActiveElapsed);
        }
      } else {
        if (stopwatch.ActiveElapsed >= options.Timeout) {
          stopwatch.Halt();
          LogSyncWaitTimedOut(_logger, perspectiveName, eventsToWait, stopwatch.ActiveElapsed.TotalMilliseconds);
          return new SyncResult(SyncOutcome.TimedOut, eventsToWait, stopwatch.ActiveElapsed);
        }
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

      // DEBUG: Log what the database returned
      if (_logger.IsEnabled(LogLevel.Debug)) {
        foreach (var r in resultsWithExpected) {
          var expectedIdsStr = string.Join(", ", r.ExpectedEventIds ?? []);
          var processedIdsStr = string.Join(", ", r.ProcessedEventIds ?? []);
          LogSyncDebugDbResult(_logger, r.StreamId, r.PendingCount, r.ProcessedCount,
            expectedIdsStr, processedIdsStr, r.IsFullySynced);
        }
      }

      // Check if all inquiries are fully synced
      if (resultsWithExpected.All(r => r.IsFullySynced)) {
        stopwatch.Halt();
        LogSyncWaitCompleted(_logger, perspectiveName, eventsToWait, stopwatch.ActiveElapsed.TotalMilliseconds);
        return new SyncResult(SyncOutcome.Synced, eventsToWait, stopwatch.ActiveElapsed);
      }

      // Wait before next poll
      await Task.Delay(pollInterval, ct);
    }

    ct.ThrowIfCancellationRequested();
    stopwatch.Halt();
    LogSyncWaitTimedOut(_logger, perspectiveName, eventsToWait, stopwatch.ActiveElapsed.TotalMilliseconds);
    return new SyncResult(SyncOutcome.TimedOut, eventsToWait, stopwatch.ActiveElapsed);
  }

  /// <inheritdoc />
  public async Task<SyncResult> WaitForStreamAsync(
      Type perspectiveType,
      Guid streamId,
      Type[]? eventTypes,
      TimeSpan timeout,
      Guid? eventIdToAwait = null,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);

    var stopwatch = _clock.StartNew();
    var perspectiveName = _getPerspectiveName(perspectiveType);

    // Build expected event IDs from four sources (in order of priority):
    // 1. Explicit eventIdToAwait parameter (for attribute-based sync with incoming event)
    // 2. Singleton ISyncEventTracker (for cross-scope sync - events tracked before DB)
    // 3. Events tracked in this scope via IScopedEventTracker
    // 4. Fall back to database discovery (no explicit IDs)

    Guid[]? expectedEventIds = null;
    var usedSingletonTracker = false; // Track if we need event-driven waiting

    // Priority 1: Use explicit event ID if provided
    if (eventIdToAwait.HasValue) {
      expectedEventIds = [eventIdToAwait.Value];
#pragma warning disable CA1848
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: Using explicit eventIdToAwait={EventId}", eventIdToAwait.Value);
      }
#pragma warning restore CA1848
    }
    // Priority 2: Use singleton ISyncEventTracker for cross-scope sync
    else if (_syncEventTracker is not null) {
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

      if (trackedSyncEvents.Count > 0) {
        expectedEventIds = trackedSyncEvents.Select(e => e.EventId).ToArray();
        usedSingletonTracker = true; // Use event-driven waiting
      }
      // NOTE: If singleton tracker exists but has no events, we DON'T return Synced.
      // Fall through to Priority 3/4 for database discovery. The event may exist
      // in the database but wasn't tracked (e.g., emitted before tracker was wired up,
      // or ITrackedEventTypeRegistry didn't include this event type).
    }
    // Priority 3: Use scoped tracker for same-scope sync
    else if (_tracker is not null) {
      var trackedEvents = _tracker.GetEmittedEvents()
        .Where(e => e.StreamId == streamId)
        .ToList();

      // Apply event type filter if specified
      if (eventTypes is { Length: > 0 }) {
        var eventTypeSet = eventTypes.ToHashSet();
        trackedEvents = trackedEvents
          .Where(e => eventTypeSet.Contains(e.EventType))
          .ToList();
      }

      // Get the specific EventIds we need to wait for
      expectedEventIds = trackedEvents.Count > 0
        ? trackedEvents.Select(e => e.EventId).ToArray()
        : null;

#pragma warning disable CA1848
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: Used SCOPED tracker - FoundCount={Count}", expectedEventIds?.Length ?? 0);
      }
    } else if (_logger.IsEnabled(LogLevel.Debug)) {
      _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: No tracker available - _syncEventTracker={HasSingleton}, _tracker={HasScoped}",
        _syncEventTracker is not null, _tracker is not null);
    }
#pragma warning restore CA1848

    LogStreamSyncWaitStarting(_logger, perspectiveName, streamId);

    // EVENT-DRIVEN WAITING: If we have expected event IDs from singleton tracker,
    // use the tracker's WaitForEventsAsync for efficient completion notification.
    // This avoids polling and provides immediate notification when events are processed.
    if (usedSingletonTracker && expectedEventIds is { Length: > 0 } && _syncEventTracker is not null) {
#pragma warning disable CA1848
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: Starting event-driven wait for {Count} events - [{Ids}]",
          expectedEventIds.Length, string.Join(", ", expectedEventIds));
      }
#pragma warning restore CA1848
      var success = await _syncEventTracker.WaitForEventsAsync(expectedEventIds, timeout, ct);
      stopwatch.Halt();

      if (success) {
        LogStreamSyncWaitCompleted(_logger, perspectiveName, streamId, expectedEventIds.Length, stopwatch.ActiveElapsed.TotalMilliseconds);
        return new SyncResult(SyncOutcome.Synced, expectedEventIds.Length, stopwatch.ActiveElapsed);
      } else {
#pragma warning disable CA1848
        if (_logger.IsEnabled(LogLevel.Debug)) {
          _logger.LogDebug("[SYNC_DEBUG] WaitForStreamAsync: Event-driven wait TIMED OUT after {Ms}ms waiting for [{Ids}]",
            stopwatch.ActiveElapsed.TotalMilliseconds, string.Join(", ", expectedEventIds));
        }
#pragma warning restore CA1848
        LogStreamSyncWaitTimedOut(_logger, perspectiveName, streamId, stopwatch.ActiveElapsed.TotalMilliseconds);
        return new SyncResult(SyncOutcome.TimedOut, expectedEventIds.Length, stopwatch.ActiveElapsed);
      }
    }

    // FALLBACK: Database polling for cases where:
    // - No events tracked in singleton tracker
    // - Need to discover events from outbox
    // - Legacy stream-wide query behavior

    // Priority 4: No explicit IDs but have EventTypes - discover from outbox (cross-scope sync)
    // Priority 5: No explicit IDs, no EventTypes - fall back to stream-wide query (legacy behavior)
    var discoverFromOutbox = expectedEventIds is null && eventTypes is { Length: > 0 };

    // Build inquiry for this stream
    // When expectedEventIds is set, we request ProcessedEventIds back to compare
    // When discoverFromOutbox is true, SQL will find events from outbox and return them
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventIds = expectedEventIds, // Specific IDs or null for discovery/stream-wide query
      IncludeProcessedEventIds = expectedEventIds is { Length: > 0 } || discoverFromOutbox,
      DiscoverPendingFromOutbox = discoverFromOutbox, // Query outbox when no explicit IDs
      // BUG FIX: Must include assembly name to match stored format ("TypeName, AssemblyName")
      // Events are stored in wh_event_store.event_type as "TypeName, AssemblyName" via normalize_event_type()
      // Using just t.FullName doesn't match because it lacks the ", AssemblyName" suffix
      EventTypeFilter = eventTypes?.Select(t => (t.FullName ?? t.Name) + ", " + t.Assembly.GetName().Name).ToArray()
    };

    // Poll database until synced or timeout
    while (!ct.IsCancellationRequested) {
      // Check timeout using debugger-aware stopwatch
      if (stopwatch.HasTimedOut(timeout)) {
        stopwatch.Halt();
        LogStreamSyncWaitTimedOut(_logger, perspectiveName, streamId, stopwatch.ActiveElapsed.TotalMilliseconds);
        return new SyncResult(SyncOutcome.TimedOut, 0, stopwatch.ActiveElapsed);
      }

      // Query database for sync status
      var batch = await _querySyncStatusAsync([inquiry], ct);
      var result = batch.SyncInquiryResults?.FirstOrDefault();

      // BUG FIX: When result is null AND we're in discovery mode (no explicit eventIds),
      // it means there are NO events matching the criteria in the database.
      // In this case, we should return Synced because there's nothing to wait for.
      // Previously, this would fall through and keep polling until timeout.
      if (result is null && expectedEventIds is null) {
        stopwatch.Halt();
        LogSyncDebugNoEventsFound(_logger, streamId);
        LogStreamSyncWaitCompleted(_logger, perspectiveName, streamId, 0, stopwatch.ActiveElapsed.TotalMilliseconds);
        return new SyncResult(SyncOutcome.Synced, 0, stopwatch.ActiveElapsed);
      }

      if (result is not null) {
        // Set ExpectedEventIds on result for IsFullySynced evaluation
        // This ensures we check that ALL expected events are processed,
        // not just that PendingCount == 0 (which could be a false positive)
        var resultWithExpected = expectedEventIds is { Length: > 0 }
          ? result with { ExpectedEventIds = expectedEventIds }
          : result;

        if (resultWithExpected.IsFullySynced) {
          stopwatch.Halt();
          var processed = result.ProcessedCount;

          LogStreamSyncWaitCompleted(_logger, perspectiveName, streamId, processed, stopwatch.ActiveElapsed.TotalMilliseconds);
          return new SyncResult(SyncOutcome.Synced, processed, stopwatch.ActiveElapsed);
        }
      }

      // Wait before next poll
      await Task.Delay(_defaultPollInterval, ct);
    }

    ct.ThrowIfCancellationRequested();
    stopwatch.Halt();
    return new SyncResult(SyncOutcome.TimedOut, 0, stopwatch.ActiveElapsed);
  }

  /// <summary>
  /// Builds sync inquiries from tracked events, grouped by stream.
  /// </summary>
  private static SyncInquiry[] _buildSyncInquiries(
      IReadOnlyList<TrackedEvent> events,
      string perspectiveName) {
    return events
      .GroupBy(e => e.StreamId)
      .Select(g => new SyncInquiry {
        StreamId = g.Key,
        PerspectiveName = perspectiveName,
        EventIds = g.Select(e => e.EventId).ToArray(),
        IncludeProcessedEventIds = true // Request processed IDs for explicit comparison
      })
      .ToArray();
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
      PerspectiveFailures = [],
      NewOutboxMessages = [],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      PerspectiveSyncInquiries = inquiries,
      Flags = WorkBatchFlags.None // Sync queries are processed regardless of flags
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
    Message = "[SYNC_DEBUG] WaitAsync: DB returned StreamId={StreamId}, PendingCount={PendingCount}, ProcessedCount={ProcessedCount}, ExpectedEventIds=[{ExpectedIds}], ProcessedEventIds=[{ProcessedIds}], IsFullySynced={IsFullySynced}"
  )]
  private static partial void LogSyncDebugDbResult(ILogger logger, Guid streamId, int pendingCount, int processedCount, string expectedIds, string processedIds, bool isFullySynced);

  [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Debug,
    Message = "[SYNC_DEBUG] WaitForStreamAsync: No events found in DB for stream={StreamId} with eventTypes. Returning Synced (nothing to wait for)."
  )]
  private static partial void LogSyncDebugNoEventsFound(ILogger logger, Guid streamId);
}
