// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs</tests>
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
/// <param name="lifecycleContextAccessor">The lifecycle context, to refuse a wait inside an Inline stage.</param>
/// <param name="timeProvider">The time source for the applied-event wait's timeout and re-read backoff (system time when null).</param>
public sealed partial class PerspectiveSyncAwaiter(
    IWorkCoordinator coordinator,
    IDebuggerAwareClock clock,
    ILogger<PerspectiveSyncAwaiter> logger,
    ISyncEventTracker syncEventTracker,
    IScopedEventTracker tracker,
    ILifecycleContextAccessor lifecycleContextAccessor,
    TimeProvider? timeProvider = null) : IPerspectiveSyncAwaiter {
  private const string TAG_SYNC_OUTCOME = "whizbang.sync.outcome";
  private const string TAG_SYNC_EVENT_COUNT = "whizbang.sync.event_count";

  /// <inheritdoc />
  public Guid AwaiterId { get; } = TrackedGuid.New();

  private readonly IScopedEventTracker _tracker = tracker;
  private readonly ISyncEventTracker _syncEventTracker = ArgumentGuard.NotNull(syncEventTracker);
  private readonly IWorkCoordinator _coordinator = ArgumentGuard.NotNull(coordinator);
  private readonly IDebuggerAwareClock _clock = ArgumentGuard.NotNull(clock);
  private readonly ILogger<PerspectiveSyncAwaiter> _logger = ArgumentGuard.NotNull(logger);
  private readonly ILifecycleContextAccessor _lifecycleContextAccessor = lifecycleContextAccessor;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <summary>
  /// The source the sync span is started on. Internal so a test can hand in a source no listener
  /// samples: the shared source has a listener whenever any test in the process is capturing spans,
  /// so whether the span exists would otherwise depend on what else is running.
  /// </summary>
  internal global::System.Diagnostics.ActivitySource ActivitySource { get; init; } = WhizbangActivitySource.Tracing;

  /// <summary>The first re-read of the applied-event ledger follows the first read by this long.</summary>
  private static readonly TimeSpan _firstLedgerReread = TimeSpan.FromMilliseconds(50);

  /// <summary>The re-read backoff doubles up to this cap.</summary>
  private static readonly TimeSpan _maxLedgerReread = TimeSpan.FromSeconds(1);


  /// <inheritdoc />
  public Task<bool> IsCaughtUpAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(options);

    if (!_tracker.IsAvailable) {
      throw new InvalidOperationException(
          "IsCaughtUpAsync requires IScopedEventTracker. Use WaitForStreamAsync for stream-based sync.");
    }

    return _isCaughtUpCoreAsync(perspectiveType, options, ct);
  }

  private async Task<bool> _isCaughtUpCoreAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct) {
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
    var results = await _querySyncStatusAsync(inquiries, ct);

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

    if (!_tracker.IsAvailable) {
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
    using var syncActivity = ActivitySource.StartActivity(
      $"PerspectiveSync {perspectiveType.Name}",
      ActivityKind.Internal);
    syncActivity?.SetTag("whizbang.sync.perspective", TypeNameFormatter.DisplayName(perspectiveType));
    syncActivity?.SetTag("whizbang.sync.timeout_ms", options.Timeout.TotalMilliseconds);

    var stopwatch = _clock.StartNew();
    var pendingEvents = _tracker.GetEmittedEvents(options.Filter);

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
        // _buildSyncInquiries sets EventIds on every inquiry it builds; PerspectiveName is required.
        var eventIdsStr = string.Join(", ", inquiry.EventIds!);
        LogSyncDebugWaiting(_logger, inquiry.StreamId, inquiry.PerspectiveName, eventIdsStr);
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
    using var syncActivity = ActivitySource.StartActivity(
      $"PerspectiveSync {perspectiveType.Name} Stream",
      ActivityKind.Internal);
    _setStreamSyncActivityTags(syncActivity, perspectiveType, streamId, timeout, eventIdToAwait);

    var perspectiveName = _getPerspectiveName(perspectiveType);

    // An explicit event this process never tracked (it arrived from another service, or it is a collective
    // event) is answered from the applied-event ledger. Reporting it synced at once was the defect (#959).
    if (eventIdToAwait is { } explicitEventId && !_isTrackedLocally(explicitEventId, streamId, perspectiveName)) {
      var ledgerResult = await _waitForAppliedCoreAsync(
        new AppliedEventInquiry(perspectiveName, explicitEventId), timeout, requireLedger: false, ct);
      if (ledgerResult is { } answered) {
        return answered;
      }
    }

    var stopwatch = _clock.StartNew();
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

  private bool _isTrackedLocally(Guid eventId, Guid streamId, string perspectiveName) =>
    _syncEventTracker.GetPendingEvents(streamId, perspectiveName).Any(e => e.EventId == eventId);

  /// <inheritdoc />
  public Task<SyncResult> WaitForAppliedAsync(
      Type perspectiveType,
      Guid eventId,
      TimeSpan timeout,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    _throwIfInsideInlineStage();
    return _waitForAppliedAsync(
      new AppliedEventInquiry(_getPerspectiveName(perspectiveType), eventId), timeout, ct);
  }

  /// <inheritdoc />
  public Task<SyncResult> WaitForAppliedAsync(
      Type perspectiveType,
      Guid streamId,
      int streamPosition,
      TimeSpan timeout,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentOutOfRangeException.ThrowIfLessThan(streamPosition, 1);
    _throwIfInsideInlineStage();
    return _waitForAppliedAsync(
      new AppliedEventInquiry(_getPerspectiveName(perspectiveType), null, streamId, streamPosition), timeout, ct);
  }

  private async Task<SyncResult> _waitForAppliedAsync(AppliedEventInquiry inquiry, TimeSpan timeout, CancellationToken ct) =>
    (await _waitForAppliedCoreAsync(inquiry, timeout, requireLedger: true, ct)).GetValueOrDefault();

  /// <summary>
  /// The applied-event wait. Registers for the in-process applied signal first, then reads the ledger on a
  /// growing backoff until the event is applied, has nothing to apply, or the timeout passes.
  /// </summary>
  /// <param name="inquiry">The perspective and the event.</param>
  /// <param name="timeout">The longest to wait.</param>
  /// <param name="requireLedger">
  /// True for <see cref="WaitForAppliedAsync(Type, Guid, TimeSpan, CancellationToken)"/>: a coordinator that
  /// cannot read the ledger leaves the in-process signal alone to answer. False for the explicit-id stream wait,
  /// which returns <see langword="null"/> instead so the caller keeps its previous behavior.
  /// </param>
  /// <param name="ct">The caller's cancellation.</param>
  private async Task<SyncResult?> _waitForAppliedCoreAsync(
      AppliedEventInquiry inquiry, TimeSpan timeout, bool requireLedger, CancellationToken ct) {
    using var syncActivity = ActivitySource.StartActivity(
      $"PerspectiveSync {inquiry.PerspectiveName} Applied",
      ActivityKind.Internal);
    syncActivity?.SetTag("whizbang.sync.perspective", inquiry.PerspectiveName);
    syncActivity?.SetTag("whizbang.sync.timeout_ms", timeout.TotalMilliseconds);

    var stopwatch = _clock.StartNew();
    using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
    var token = waitCts.Token;
    var eventId = inquiry.EventId;
    // Registered before the first read, so an apply committed between the read and the wait is not missed.
    Task? appliedHere = _whenAppliedHereOrNull(eventId, inquiry.PerspectiveName, token);
    var reread = _firstLedgerReread;
    try {
      while (true) {
        // The re-read timer starts before the read, so the backoff is measured from the read's start.
        var nextRead = Task.Delay(reread, _timeProvider, token);
        var status = await _coordinator.GetAppliedEventStatusAsync(inquiry with { EventId = eventId }, token);
        if (status is null) {
          return await _appliedWithoutLedgerAsync(requireLedger, appliedHere, inquiry, stopwatch, syncActivity);
        }
        if (status.Value.IsSettled) {
          return _appliedResult(_settledOutcome(status.Value.State), inquiry, stopwatch, syncActivity);
        }
        if (appliedHere is null && status.Value.EventId is { } resolved) {
          eventId = resolved;
          appliedHere = _whenAppliedHereAsync(resolved, inquiry.PerspectiveName, token);
        }
        if (await _appliedBeforeRereadAsync(appliedHere, nextRead)) {
          return _appliedResult(SyncOutcome.Synced, inquiry, stopwatch, syncActivity);
        }
        reread = _nextReread(reread);
      }
    } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
      LogSyncWaitTimedOut(_logger, inquiry.PerspectiveName, 1, stopwatch.ActiveElapsed.TotalMilliseconds);
      return _appliedResult(SyncOutcome.TimedOut, inquiry, stopwatch, syncActivity);
    } finally {
      // Releases the in-process waiters this wait registered; disposing alone would leave them registered.
      await waitCts.CancelAsync();
    }
  }

  /// <summary>
  /// The wait when the coordinator cannot read the ledger: the in-process signal alone answers, which needs the
  /// event's id; an event named only by stream position cannot be resolved without the ledger.
  /// </summary>
  /// <remarks>
  /// Returns <see langword="null"/> when the caller does not require the ledger (the explicit-id stream wait), so it
  /// keeps its previous behavior.
  /// </remarks>
  private async Task<SyncResult?> _appliedWithoutLedgerAsync(
      bool requireLedger, Task? appliedHere, AppliedEventInquiry inquiry, IActiveStopwatch stopwatch, Activity? syncActivity) {
    if (!requireLedger) {
      return null;
    }
    if (appliedHere is null) {
      throw new NotSupportedException(
        $"{_coordinator.GetType().Name} cannot read the applied-event ledger, so an event named by stream "
        + "position cannot be resolved. Name the event by id, or use a work coordinator that reads the ledger.");
    }
    await appliedHere;
    return _appliedResult(SyncOutcome.Synced, inquiry, stopwatch, syncActivity);
  }

  /// <summary>The in-process applied signal for an event named by id; none yet for one named by stream position.</summary>
  private Task? _whenAppliedHereOrNull(Guid? eventId, string perspectiveName, CancellationToken token) =>
    eventId is { } known ? _whenAppliedHereAsync(known, perspectiveName, token) : null;

  /// <summary>A settled event was applied, or had nothing to apply.</summary>
  private static SyncOutcome _settledOutcome(AppliedEventState state) =>
    state == AppliedEventState.Applied ? SyncOutcome.Synced : SyncOutcome.NoPendingEvents;

  /// <summary>The next ledger re-read interval: doubled, up to the ceiling.</summary>
  private static TimeSpan _nextReread(TimeSpan reread) =>
    reread * 2 < _maxLedgerReread ? reread * 2 : _maxLedgerReread;

  /// <summary>True when the in-process applied signal came first; false when it is time to read the ledger again.</summary>
  private static async Task<bool> _appliedBeforeRereadAsync(Task? appliedHere, Task nextRead) {
    var woke = await Task.WhenAny(appliedHere ?? nextRead, nextRead);
    await woke;
    return woke == appliedHere;
  }

  /// <summary>Completes when this process applies the event for the perspective, or for the collective sink.</summary>
  private async Task _whenAppliedHereAsync(Guid eventId, string perspectiveName, CancellationToken token) {
    var first = await Task.WhenAny(
      _syncEventTracker.WhenAppliedAsync(eventId, perspectiveName, token),
      _syncEventTracker.WhenAppliedAsync(eventId, CollectiveRouting.SINK_PERSPECTIVE_NAME, token));
    await first;
  }

  private static SyncResult _appliedResult(
      SyncOutcome outcome, AppliedEventInquiry inquiry, IActiveStopwatch stopwatch, Activity? syncActivity) {
    stopwatch.Halt();
    _setSyncActivityOutcome(syncActivity, outcome.ToString(), 1, stopwatch.ActiveElapsed);
    return new SyncResult(outcome, 1, stopwatch.ActiveElapsed, EventsTracked: 1, PerspectiveName: inquiry.PerspectiveName);
  }

  private static void _setStreamSyncActivityTags(
      Activity? syncActivity, Type perspectiveType, Guid streamId, TimeSpan timeout, Guid? eventIdToAwait) {
    syncActivity?.SetTag("whizbang.sync.perspective", TypeNameFormatter.DisplayName(perspectiveType));
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
  /// Queries the database for sync status using the new-path
  /// <see cref="IWorkCoordinator.ResolveSyncInquiriesAsync"/> SQL function.
  /// </summary>
  private async Task<IReadOnlyList<SyncInquiryResult>> _querySyncStatusAsync(
      SyncInquiry[] inquiries,
      CancellationToken ct) {
    return await _coordinator.ResolveSyncInquiriesAsync(inquiries, ct);
  }

  /// <summary>
  /// Gets the perspective name from the perspective type.
  /// Delegates to <see cref="TypeNameFormatter.GetPerspectiveName"/> for consistent
  /// CLR-format naming across runtime code, generators, and database storage.
  /// </summary>
  private static string _getPerspectiveName(Type perspectiveType) {
    return TypeNameFormatter.GetPerspectiveName(perspectiveType);
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
    if (_lifecycleContextAccessor.Current is { } ctx && !ctx.CurrentStage.IsDetached()) {
      throw new InvalidOperationException(
        "WaitForStreamAsync/WaitAsync cannot be called inside an Inline lifecycle " +
        $"receptor (current stage: {ctx.CurrentStage}). This would deadlock the work " +
        "coordinator. Use a Detached stage or event enrichment instead.");
    }
  }
}
