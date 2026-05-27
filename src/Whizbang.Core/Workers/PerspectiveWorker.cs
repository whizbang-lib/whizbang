using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background worker that processes perspective cursors using IWorkCoordinator.
/// Polls for event store streams with new events since last checkpoint,
/// invokes perspectives, and tracks checkpoint progress per stream.
/// Uses lease-based coordination for reliable perspective processing across instances.
/// </summary>
/// <docs>operations/workers/perspective-worker</docs>
#pragma warning disable S107 // Constructor uses DI injection — many parameters are idiomatic
public partial class PerspectiveWorker(
  IServiceInstanceProvider instanceProvider,
  IServiceScopeFactory scopeFactory,
  IOptions<PerspectiveWorkerOptions> options,
  IOptionsMonitor<TracingOptions>? tracingOptions = null,
  IPerspectiveCompletionStrategy? completionStrategy = null,
  IEventTypeProvider? eventTypeProvider = null,
  IPerspectiveSyncSignaler? syncSignaler = null,
  ISyncEventTracker? syncEventTracker = null,
  ILogger<PerspectiveWorker>? logger = null,
  PerspectiveMetrics? metrics = null,
  IPerspectiveSnapshotStore? snapshotStore = null,
  IPerspectiveStreamLocker? streamLocker = null,
  IOptions<PerspectiveStreamLockOptions>? streamLockOptions = null,
  IProcessedEventCacheObserver? processedEventCacheObserver = null,
  TimeProvider? timeProvider = null,
  LifecycleCoordinatorMetrics? coordinatorMetrics = null,
  IWorkChannelWriter? workChannelWriter = null,
  IOptions<PerspectiveRewindOptions>? rewindOptions = null,
  IPerspectiveChannelWriter? perspectiveChannelWriter = null,
  IPerspectiveCompletionChannel? perspectiveCompletionChannel = null,
  IFailureChannel? failureChannel = null,
  ILeaseRenewalChannel? leaseRenewalChannel = null,
  IPerspectiveDrainChannel? perspectiveDrainChannel = null,
  RecentlyProcessedEventCache? recentlyProcessedEventCache = null,
  IOptions<LeaseHandleOptions>? leaseHandleOptions = null,
  IOptions<LeaseRenewalWorkerOptions>? leaseRenewalOptions = null
) : BackgroundService {
#pragma warning restore S107
  private const string METRIC_TAG_PERSPECTIVE_NAME = "perspective_name";

  private readonly ConcurrentBag<Task> _detachedTasks = [];
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions = tracingOptions;
  private IEventTypeProvider? _eventTypeProvider = eventTypeProvider;
  private readonly IPerspectiveSyncSignaler? _syncSignaler = syncSignaler;
  private readonly ISyncEventTracker? _syncEventTracker = syncEventTracker;
  private readonly ILogger<PerspectiveWorker> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PerspectiveWorker>.Instance;
  private readonly PerspectiveMetrics? _metrics = metrics;
  private readonly PerspectiveWorkerOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
  private readonly IPerspectiveCompletionStrategy _completionStrategy = completionStrategy ?? new BatchedCompletionStrategy(
    retryTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.RetryTimeoutSeconds),
    backoffMultiplier: (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.EnableExponentialBackoff
      ? (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.BackoffMultiplier
      : 1.0,
    maxTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.MaxBackoffSeconds)
  );

  private readonly IPerspectiveSnapshotStore? _snapshotStore = snapshotStore;
  private readonly PerspectiveRewindOptions _rewindOptions = rewindOptions?.Value ?? new PerspectiveRewindOptions();
  private readonly ILogger _startupScanLog = scopeFactory.CreateScope().ServiceProvider
    .GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Workers.PerspectiveStartupScan")
    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
  private readonly IPerspectiveStreamLocker? _streamLocker = streamLocker;
  private readonly PerspectiveStreamLockOptions _streamLockOptions = streamLockOptions?.Value ?? new PerspectiveStreamLockOptions();

  // Perspective event completions (WorkIds to delete from wh_perspective_events)
  private readonly System.Collections.Concurrent.ConcurrentQueue<PerspectiveEventCompletion> _pendingEventCompletions = new();

  // Phase C channel dependencies — when wired, perspective work flows through ClaimWorker → these channels
  // instead of being polled directly via ProcessWorkBatchAsync. Currently dormant; ExecuteAsync still uses
  // the legacy poll path until commit B switches the main loop. See plans/we-need-to-study-iridescent-gem.md
  // for the multi-commit migration sequence.
  private readonly IPerspectiveChannelWriter? _perspectiveChannelWriter = perspectiveChannelWriter;
  private readonly IPerspectiveCompletionChannel? _perspectiveCompletionChannel = perspectiveCompletionChannel;
  private readonly IFailureChannel? _failureChannel = failureChannel;
  private readonly ILeaseRenewalChannel? _leaseRenewalChannel = leaseRenewalChannel;
  private readonly IPerspectiveDrainChannel? _perspectiveDrainChannel = perspectiveDrainChannel;
  private readonly RecentlyProcessedEventCache? _recentlyProcessedEventCache = recentlyProcessedEventCache;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  private readonly LeaseHandleOptions _leaseHandleOptions = leaseHandleOptions?.Value ?? new LeaseHandleOptions();
  private readonly LeaseRenewalWorkerOptions _leaseRenewalOptions = leaseRenewalOptions?.Value ?? new LeaseRenewalWorkerOptions();

  // Cache of streams that have been bootstrapped this session (skip re-check)
  private readonly ConcurrentDictionary<(Guid StreamId, string PerspectiveName), byte> _bootstrappedThisSession = new();

  // Two-phase TTL cache to prevent duplicate Apply when SQL re-delivers events during batched completion window
  private readonly ProcessedEventCache _processedEventCache = new(
    TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.LeaseSeconds),
    timeProvider: timeProvider,
    observer: processedEventCacheObserver
  );

  // Registry-based map: event type (CLR format) → all perspective CLR names that handle it.
  // Built once at startup from IPerspectiveRunnerRegistry. Used to register complete WhenAll
  // expectations per event so PostAllPerspectivesDetached fires once after ALL perspectives complete,
  // not once per batch cycle.
  private Dictionary<string, IReadOnlyList<string>>? _perspectivesPerEventType;


  // Cursor position cache for drain mode — eliminates redundant GetPerspectiveCursorAsync DB calls
  private readonly PerspectiveCursorCache _cursorCache = new();

  /// <summary>
  /// Per-batch accumulators + lookups that drain-mode helpers thread through together.
  /// Groups the shared batch state (raw rows, type-name cache, processed-event and
  /// is-new dictionaries, and the optional lifecycle coordinator) so drain helpers
  /// don't need long parameter lists to pass identical data.
  /// </summary>
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Positional record whose purpose is to group drain-mode batch accumulators — it is itself the fix for S107 on methods that would otherwise take these values individually.")]
  private readonly record struct DrainBatchContext(
    ILookup<Guid, StreamEventData> RawByEventId,
    Dictionary<Type, string> TypeNameCache,
    ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> BatchProcessedEvents,
    ConcurrentDictionary<Guid, bool> BatchIsNewByEventId,
    ILifecycleCoordinator? LifecycleCoordinator);

  // Metrics tracking
  private int _consecutiveEmptyPolls;
  private bool _isIdle = true;  // Start in idle state
  private int _batchCycleCount;

  // Wake signal: allows external callers to interrupt the polling delay
  // so the worker processes new perspective events immediately.
  private readonly SemaphoreSlim _pollWakeSignal = new(0, 1);
  private int _wakeSignaled;  // Guard to prevent SemaphoreFullException on redundant wake calls

  // PostLifecycle runs fire-and-forget in a dedicated scope so the next drain cycle's
  // claim/apply phases can overlap with the prior cycle's PostLifecycle handlers. The
  // next cycle awaits this task before firing its own PostLifecycle so invocations
  // remain ordered. On shutdown, ExecuteAsync awaits this task to drain.
  private Task? _pendingPostLifecycle;

  /// <summary>
  /// Test-only accessor for the in-flight PostLifecycle task. Tests that assert
  /// PostLifecycle stages fired for a batch should await this (when non-null) after
  /// <see cref="OnBatchCycleComplete"/> signals to observe the background stages.
  /// </summary>
  internal Task? PendingPostLifecycle => _pendingPostLifecycle;

  /// <summary>
  /// Gets the number of consecutive empty work polls (no perspective work returned).
  /// Resets to 0 when work is found.
  /// </summary>
  public int ConsecutiveEmptyPolls => _consecutiveEmptyPolls;

  /// <summary>
  /// Gets whether the worker is currently in idle state (no work being processed).
  /// </summary>
  public bool IsIdle => _isIdle;

  /// <summary>
  /// Event fired when work processing starts (idle → active transition).
  /// Fires when work appears after consecutive empty polls.
  /// </summary>
  public event WorkProcessingStartedHandler? OnWorkProcessingStarted;

  /// <summary>
  /// Event fired when work processing becomes idle (active → idle transition).
  /// Fires after N consecutive polls returned no work (configured via IdleThresholdPolls).
  /// Useful for integration tests to wait for perspective processing completion.
  /// </summary>
  public event WorkProcessingIdleHandler? OnWorkProcessingIdle;

  /// <summary>
  /// Signals the worker to wake immediately and poll for new perspective events,
  /// instead of waiting for the next scheduled polling interval.
  /// </summary>
  /// <remarks>
  /// Use this when new events have been written to the event store (e.g., after a
  /// transport consumer processes a received message) and you want perspectives
  /// to materialize immediately. Safe to call from any thread; redundant calls are harmless.
  /// </remarks>
  /// <docs>operations/workers/perspective-worker#immediate-poll</docs>
  public void RequestImmediatePoll() {
    if (Interlocked.CompareExchange(ref _wakeSignaled, 1, 0) == 0) {
      _pollWakeSignal.Release();
    }
  }

  /// <summary>
  /// Event fired after a complete batch cycle finishes, including all phases:
  /// drain mode processing, lifecycle stages (PostAllPerspectives, PostLifecycle),
  /// and metrics recording. Fires once per worker tick regardless of whether work was found.
  /// </summary>
  /// <remarks>
  /// Use for deterministic test synchronization when verifying lifecycle stages that fire
  /// in Phase 5 (after perspective processing). Also useful in production for batch-level
  /// monitoring and alerting on processing cadence.
  /// </remarks>
  /// <docs>operations/workers/perspective-worker#processing-hooks</docs>
  public event Action? OnBatchCycleComplete;

  /// <summary>
  /// Event fired after a perspective successfully processes events for a stream.
  /// Fires synchronously on the perspective worker thread after completion buffering.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Use this hook for deterministic test synchronization (replaces CountingPerspectiveReceptor
  /// and PerspectiveCompletionWaiter which depend on PostPerspectiveInline lifecycle stage).
  /// </para>
  /// <para>
  /// Also useful in production for monitoring perspective processing throughput,
  /// triggering downstream actions after materialization, or building custom completion gates.
  /// </para>
  /// </remarks>
  /// <docs>operations/workers/perspective-worker#processing-hooks</docs>
  public event PerspectiveEventProcessedHandler? OnPerspectiveEventProcessed;

  /// <summary>
  /// Groups per-stream perspective processing parameters that travel together through lifecycle phases.
  /// </summary>
  private readonly record struct PerspectiveStreamContext(
    Guid StreamId,
    string PerspectiveName,
    Guid? LastProcessedEventId,
    IServiceProvider ScopedProvider);

  /// <inheritdoc/>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogWorkerStarting(_logger, _instanceProvider.InstanceId, _instanceProvider.ServiceName, _instanceProvider.HostName, _instanceProvider.ProcessId, _options.PollingIntervalMilliseconds);

    await _initializePerspectiveRegistryAsync();
    _processInitialCheckpoints();
    await _reconcileOrphanedLifecyclesAsync(stoppingToken);
    await _scanAndRepairRewindsOnStartupAsync(stoppingToken);

    // Subscribe to new perspective work signals so we poll immediately when events arrive
    if (workChannelWriter is not null) {
      workChannelWriter.OnNewPerspectiveWorkAvailable += RequestImmediatePoll;
    }

    // The work-pump decomposition migrated perspective traffic to the channel architecture
    // (ClaimWorker → IPerspectiveChannelWriter / IPerspectiveDrainChannel → here). Channel deps
    // are optional in the constructor only so existing test fixtures compile unchanged; runtime
    // requires them. Wire them by calling AddWhizbang() (which auto-invokes AddWhizbangWorkers).
    if (_perspectiveChannelWriter is null
        || _perspectiveCompletionChannel is null
        || _failureChannel is null) {
      throw new InvalidOperationException(
        "PerspectiveWorker requires IPerspectiveChannelWriter, IPerspectiveCompletionChannel, " +
        "and IFailureChannel to be wired via AddWhizbangWorkers (called automatically by " +
        "AddWhizbang). The legacy ProcessWorkBatchAsync poll path was removed.");
    }

    try {
      // Slice 17: spawn N parallel consumer loops. Each independently reads batches from the
      // shared channels and processes them. ProcessChannelBatchAsync itself fans out per
      // (streamId, perspectiveName) up to MaxConcurrentPerspectives, so outer × inner gives
      // the steady-state concurrency ceiling. With outer=1 (pre-slice-17) the batch loop was
      // serial — when batch N was processing, batch N+1's items piled up in the channel until
      // batch N completed. Multiple consumers race for items, so different streams flow in
      // parallel without each having to wait for a prior batch to finish.
      var consumerCount = Math.Max(1, _options.MaxConcurrentDrainConsumers);
      if (consumerCount == 1) {
        await _runChannelConsumerLoopAsync(stoppingToken).ConfigureAwait(false);
      } else {
        var consumers = new Task[consumerCount];
        for (var i = 0; i < consumerCount; i++) {
          consumers[i] = Task.Run(() => _runChannelConsumerLoopAsync(stoppingToken), stoppingToken);
        }
        try {
          await Task.WhenAll(consumers).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          // expected on shutdown
        }
      }
    } finally {
      // Graceful shutdown: drain any in-flight PostLifecycle task so background work
      // completes before the host disposes scoped services (DbContext, etc.) out from
      // under it. Stage guards ensure idempotence if the task already finished.
      // `finally` so OCE propagating out of the loop still runs the drain.
      var finalPending = Interlocked.Exchange(ref _pendingPostLifecycle, null);
      if (finalPending is not null) {
        try {
          await finalPending;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogPriorPostLifecycleFaulted(_logger, ex);
        }
      }
    }

    LogWorkerStopping(_logger);
  }


  /// <summary>
  /// Phase C channel-consumer loop: blocks on the perspective channel reader, coalesces incoming
  /// items into batches (preserving the "PostLifecycle once per batch" semantics), and processes
  /// each batch via <see cref="ProcessChannelBatchAsync"/>. Replaces the legacy SQL polling loop
  /// when channels are wired.
  /// </summary>
  private async Task _runChannelConsumerLoopAsync(CancellationToken stoppingToken) {
    var workReader = _perspectiveChannelWriter!.Reader;
    var drainReader = _perspectiveDrainChannel?.Reader;
    var drainBatcherOpts = _options.DrainBatcher;

    while (!stoppingToken.IsCancellationRequested) {
      // Block until either channel has work. WaitToReadAsync returns true when an item is
      // available (or false when the channel is closed). We race both readers to avoid
      // starving drain mode behind a quiet perspective channel (or vice versa). A timeout
      // task keeps the database-readiness counter ticking even when no work arrives — the
      // legacy poll loop did this naturally; the channel architecture needs it explicit.
      var workWait = workReader.WaitToReadAsync(stoppingToken).AsTask();
      var drainWait = drainReader is null
        ? new TaskCompletionSource<bool>().Task // never completes — only workReader is consulted
        : drainReader.WaitToReadAsync(stoppingToken).AsTask();
      var idleTimeout = Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);

      try {
        await Task.WhenAny(workWait, drainWait, idleTimeout).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      }

      if (stoppingToken.IsCancellationRequested) {
        break;
      }

      // Drain whatever is currently queued on both channels (non-blocking after the wait).
      var workBatch = new List<PerspectiveWork>(_options.MaxStreamsPerBatch);
      var drainStreamIds = new List<Guid>();

      while (workBatch.Count < _options.MaxStreamsPerBatch && workReader.TryRead(out var item)) {
        workBatch.Add(item);
      }
      if (drainReader is not null) {
        while (drainStreamIds.Count < drainBatcherOpts.MaxSize && drainReader.TryRead(out var streamId)) {
          drainStreamIds.Add(streamId);
        }
      }

      if (workBatch.Count == 0 && drainStreamIds.Count == 0) {
        // Idle tick: no work after wait. Flush any completions buffered from the prior batch
        // (the legacy poll loop flushed at the START of every cycle; the channel architecture
        // only triggers ProcessChannelBatchAsync when work arrives, so without this flush,
        // post-batch completions could sit in the queue forever).
        await _flushPendingCompletionsToChannelsAsync(stoppingToken).ConfigureAwait(false);
        // Track empty-poll state the way the legacy poll loop did so OnWorkProcessingIdle
        // fires + ConsecutiveEmptyPolls reflects reality.
        _updateWorkStateTracking(hasWork: false);
        continue;
      }

      // Sliding-window batching on the DRAIN channel only. After collecting the initial
      // drain items, wait up to SlidingWindow for additional stream_id signals to arrive,
      // bounded by MaxWait. This eliminates the JDX 2026-05-04 cursor-inversion symptom:
      // events arriving at the BFF inbox in two clumps milliseconds apart now accumulate
      // into one drain batch so the per-stream fetch returns events in monotonic order.
      // The work channel is NOT batched here — its dedup semantics depend on per-cycle
      // processing of PerspectiveWork items.
      if (drainReader is not null && drainStreamIds.Count > 0
          && drainStreamIds.Count < drainBatcherOpts.MaxSize) {
        await _accumulateDrainSignalsWithinWindowAsync(
          drainReader, drainStreamIds, drainBatcherOpts, stoppingToken).ConfigureAwait(false);
      }

      try {
        await ProcessChannelBatchAsync(workBatch, drainStreamIds, stoppingToken).ConfigureAwait(false);
        _periodicStaleTrackingCleanup();
        await _periodicGatherStatisticsAsync(stoppingToken).ConfigureAwait(false);
      } catch (ObjectDisposedException) {
        break;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogErrorProcessingCheckpoints(_logger, ex);
        throw;
      }
    }
  }

  /// <summary>
  /// Sliding-window accumulator for drain channel stream_id signals. After the initial drain
  /// has captured at least one stream_id, this method waits up to <c>SlidingWindow</c> for
  /// more signals (resetting on each new arrival) so the resulting batch represents a
  /// coherent set of streams whose events landed in <c>wh_perspective_events</c> close in
  /// time. Bounded by <c>MaxWait</c> from the first arrival and <c>MaxSize</c> on count.
  /// </summary>
  private static async Task _accumulateDrainSignalsWithinWindowAsync(
      System.Threading.Channels.ChannelReader<Guid> drainReader,
      List<Guid> drainStreamIds,
      SlidingWindowBatcherOptions opts,
      CancellationToken stoppingToken) {
    var firstArrival = TimeProvider.System.GetTimestamp();
    var lastArrival = firstArrival;

    while (drainStreamIds.Count < opts.MaxSize) {
      var elapsedSinceLast = TimeProvider.System.GetElapsedTime(lastArrival);
      var elapsedSinceFirst = TimeProvider.System.GetElapsedTime(firstArrival);
      var slidingRemaining = opts.SlidingWindow - elapsedSinceLast;
      var maxWaitRemaining = opts.MaxWait - elapsedSinceFirst;
      var waitFor = slidingRemaining < maxWaitRemaining ? slidingRemaining : maxWaitRemaining;
      if (waitFor <= TimeSpan.Zero) {
        return;
      }

      using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
      var arrivalTask = drainReader.WaitToReadAsync(waitCts.Token).AsTask();
      var timerTask = Task.Delay(waitFor, waitCts.Token);
      var completed = await Task.WhenAny(arrivalTask, timerTask).ConfigureAwait(false);
      await waitCts.CancelAsync();

      if (stoppingToken.IsCancellationRequested) {
        return;
      }
      if (completed == timerTask) {
        return;
      }

      var moreDrained = false;
      while (drainStreamIds.Count < opts.MaxSize && drainReader.TryRead(out var streamId)) {
        drainStreamIds.Add(streamId);
        moreDrained = true;
      }
      if (moreDrained) {
        lastArrival = TimeProvider.System.GetTimestamp();
      }
    }
  }

  private async Task _initializePerspectiveRegistryAsync() {
    await using var startupScope = _scopeFactory.CreateAsyncScope();
    var registry = startupScope.ServiceProvider.GetService<IPerspectiveRunnerRegistry>();
    if (registry == null) {
      LogPerspectiveRegistryNotAvailableAtStartup(_logger);
      return;
    }

    var registeredPerspectives = registry.GetRegisteredPerspectives();
    if (registeredPerspectives.Count == 0) {
      LogNoPerspectivesRegistered(_logger);
      return;
    }

    LogRegisteredPerspectivesHeader(_logger, registeredPerspectives.Count);
    if (_logger.IsEnabled(LogLevel.Information)) {
      foreach (var p in registeredPerspectives) {
        var eventTypesStr = string.Join(", ", p.EventTypes);
        LogRegisteredPerspective(_logger, p.ClrTypeName, p.ModelType, p.EventTypes.Count, eventTypesStr);
      }
    }

    _perspectivesPerEventType = _buildPerspectivesPerEventTypeMap(registeredPerspectives);
  }

  private static Dictionary<string, IReadOnlyList<string>> _buildPerspectivesPerEventTypeMap(
    IReadOnlyList<PerspectiveRegistrationInfo> registeredPerspectives) {
    var map = new Dictionary<string, List<string>>();
    foreach (var p in registeredPerspectives) {
      foreach (var eventType in p.EventTypes) {
        if (!map.TryGetValue(eventType, out var list)) {
          list = [];
          map[eventType] = list;
        }
        list.Add(p.ClrTypeName);
      }
    }
    return map.ToDictionary(
      kvp => kvp.Key,
      kvp => (IReadOnlyList<string>)kvp.Value);
  }

  private void _processInitialCheckpoints() {
    LogCheckingPendingCheckpoints(_logger);
    // Schema-ready gate has already been awaited in ExecuteAsync before this point.
    // ClaimWorker feeds work to the channel reader; the main loop picks up any
    // leftover work as soon as it starts.
    LogInitialCheckpointProcessingComplete(_logger);
  }

  private int _statsGaugeCounter;

  private async Task _periodicGatherStatisticsAsync(CancellationToken ct) {
    // Gather expensive stats every 60 ticks (~60 seconds)
    // These are COUNT(*) queries that we don't want on the hot path
    if (++_statsGaugeCounter % 60 != 0) {
      return;
    }

    try {
      await using var scope = _scopeFactory.CreateAsyncScope();
      var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      var stats = await workCoordinator.GatherStatisticsAsync(ct);
      _metrics?.SetPendingEvents(stats.PendingPerspectiveEvents);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Don't let gauge failure interrupt the main loop
      // Swallow — periodic stats gathering is non-critical
    }
  }

  private void _periodicStaleTrackingCleanup() {
    if (++_batchCycleCount % 10 != 0) {
      return;
    }

    using var cleanupScope = _scopeFactory.CreateScope();
    var lifecycleCoordinator = cleanupScope.ServiceProvider.GetService<ILifecycleCoordinator>();
    var cleaned = lifecycleCoordinator?.CleanupStaleTracking(TimeSpan.FromMinutes(5)) ?? 0;
    if (cleaned > 0) {
      LogStaleTrackingCleaned(_logger, cleaned);
    }
  }

  /// <summary>
  /// Reconciles orphaned lifecycle events at startup.
  /// Finds events where all perspectives completed but PostLifecycle never fired
  /// (e.g., due to process crash) and replays the lifecycle stages.
  /// </summary>
  private async Task _reconcileOrphanedLifecyclesAsync(CancellationToken ct) {
    if (_perspectivesPerEventType is null || _perspectivesPerEventType.Count == 0) {
      return;
    }

    try {
      using var scope = _scopeFactory.CreateScope();
      var workCoordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      var lifecycleCoordinator = scope.ServiceProvider.GetService<ILifecycleCoordinator>();

      if (workCoordinator is null || lifecycleCoordinator is null) {
        return;
      }

      var orphaned = await workCoordinator.GetOrphanedLifecycleEventsAsync(
        _perspectivesPerEventType, TimeSpan.FromMinutes(30), ct);

      if (orphaned.Count == 0) {
        return;
      }

      LogReconciliationStarting(_logger, orphaned.Count);

      foreach (var orphan in orphaned) {
        try {
          var tracking = lifecycleCoordinator.BeginTracking(
            orphan.EventId, orphan.Envelope,
            LifecycleStage.PostAllPerspectivesDetached, MessageSource.Local,
            orphan.StreamId);

          await _establishSecurityContextAsync(orphan.Envelope, scope.ServiceProvider, ct);
          await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesDetached, scope.ServiceProvider, ct);
          await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesInline, scope.ServiceProvider, ct);
          await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, scope.ServiceProvider, ct);
          await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, ct);

          await workCoordinator.RecordLifecycleCompletionAsync(orphan.EventId, ct);
          LogReconciliationCompleted(_logger, orphan.EventId);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogReconciliationError(_logger, ex, orphan.EventId);
        }
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogReconciliationFailed(_logger, ex);
    }
  }

  /// <summary>
  /// Scans for streams needing rewind on startup and processes them.
  /// In Blocking mode, keeps processing work batches until no RewindRequired cursors remain.
  /// In Background mode, logs the summary and lets normal polling handle them.
  /// </summary>
  /// <docs>fundamentals/perspectives/rewind#startup-scan</docs>
  private async Task _scanAndRepairRewindsOnStartupAsync(CancellationToken ct) {
    if (!_rewindOptions.StartupScanEnabled) {
      return;
    }

    try {
      // Query cursors with RewindRequired flag
      await using var scope = _scopeFactory.CreateAsyncScope();
      var workCoordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (workCoordinator is null) {
        return;
      }

      var rewindCursors = await workCoordinator.GetCursorsRequiringRewindAsync(ct);
      if (rewindCursors.Count == 0) {
        PerspectiveStartupScanLog.LogStartupRewindScanClean(_startupScanLog);
        return;
      }

      var streamCount = rewindCursors.Select(c => c.StreamId).Distinct().Count();
      var perspectiveCount = rewindCursors.Count;
      PerspectiveStartupScanLog.LogStartupRewindScanStarted(_startupScanLog, streamCount, perspectiveCount);

      if (_rewindOptions.StartupRewindMode == RewindStartupMode.Blocking) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Channel architecture: ClaimWorker drives claims in the background; our main loop
        // reads from the channel. Wait for ClaimWorker + the channel consumer to drain rewinds
        // by re-querying. The legacy _processWorkBatchAsync pump was removed.
        const int maxIterations = 100;
        for (var i = 0; i < maxIterations; i++) {
          await Task.Delay(TimeSpan.FromMilliseconds(_options.PollingIntervalMilliseconds), ct);
          rewindCursors = await workCoordinator.GetCursorsRequiringRewindAsync(ct);
          if (rewindCursors.Count == 0) {
            break;
          }
        }
        PerspectiveStartupScanLog.LogStartupRewindScanCompleted(_startupScanLog, streamCount, perspectiveCount, (long)sw.Elapsed.TotalMilliseconds);
      }
      // Background mode: normal channel-consumer loop will pick them up.
    } catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException) {
      PerspectiveStartupScanLog.LogStartupRewindScanError(_startupScanLog, ex);
    }
  }

  /// <summary>
  /// Records a durable lifecycle completion marker for crash recovery.
  /// </summary>
  private static async Task _recordLifecycleCompletionAsync(
    Guid eventId,
    IServiceProvider scopedProvider,
    CancellationToken ct) {
    var workCoordinator = scopedProvider.GetService<IWorkCoordinator>();
    if (workCoordinator is not null) {
      await workCoordinator.RecordLifecycleCompletionAsync(eventId, ct);
    }
  }


  /// <summary>
  /// Channel-consumer entry point: processes a batch of perspective work items pulled from
  /// <see cref="IPerspectiveChannelWriter"/> (fed by <c>ClaimWorker</c>). Routes completions
  /// and failures through the channel surfaces (fire-and-forget); acks are local.
  /// </summary>
  /// <remarks>
  /// Drain mode is supported via the overload that takes drain stream IDs (sourced from
  /// <see cref="IPerspectiveDrainChannel"/>).
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/claim-loop</docs>
  internal Task ProcessChannelBatchAsync(
    List<PerspectiveWork> workItems, CancellationToken cancellationToken)
    => ProcessChannelBatchAsync(workItems, [], cancellationToken);

  /// <summary>
  /// Channel-consumer entry point that handles both normal per-event work AND drain-mode
  /// stream IDs. Drain stream IDs route through <see cref="_processDrainModeStreamsAsync"/>
  /// (batched fetch + RunWithEventsAsync) before per-event work is processed — same ordering
  /// as the legacy poll path.
  /// </summary>
  internal async Task ProcessChannelBatchAsync(
    List<PerspectiveWork> workItems, List<Guid> drainStreamIds, CancellationToken cancellationToken) {
    if (_perspectiveCompletionChannel is null || _failureChannel is null) {
      throw new InvalidOperationException(
        "PerspectiveWorker channel mode requires IPerspectiveCompletionChannel and IFailureChannel " +
        "to be wired. Did you call AddWhizbangWorkers()?");
    }

    var batchSw = System.Diagnostics.Stopwatch.StartNew();
    var parentContext = Activity.Current?.Context ?? default;
    var enableBatchSpan = _tracingOptions?.CurrentValue.EnableWorkerBatchSpans ?? false;
    using var batchActivity = enableBatchSpan
      ? WhizbangActivitySource.Tracing.StartActivity("PerspectiveProcessWorker ProcessChannelBatch", ActivityKind.Internal)
      : null;
    if (batchActivity is not null) {
      batchActivity.SetTag("whizbang.worker", "PerspectiveProcessWorker");
      batchActivity.SetTag("whizbang.service.name", _instanceProvider.ServiceName);
      batchActivity.SetTag("whizbang.instance.id", _instanceProvider.InstanceId.ToString());
      batchActivity.SetTag("whizbang.batch.size", workItems.Count);
    }
    var effectiveParent = batchActivity?.Context ?? parentContext;

    await using var scope = _scopeFactory.CreateAsyncScope();
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
    var lifecycleCoordinator = scope.ServiceProvider.GetService<ILifecycleCoordinator>();

    _eventTypeProvider ??= scope.ServiceProvider.GetService<IEventTypeProvider>();
    _processedEventCache.EvictExpired();

    // Flush pending completions/failures through the new channels (fire-and-forget).
    // Returns the count we sent so we can ack the strategy locally — no SQL response to wait on.
    var (sentCompletionCount, sentFailureCount) = await _flushPendingCompletionsToChannelsAsync(
      cancellationToken).ConfigureAwait(false);

    // Synthesize a WorkBatch shaped to feed the existing reconciliation + processing helpers.
    // Outbox/inbox lists stay empty — those categories have their own workers.
    var workBatch = new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = workItems,
      PerspectiveStreamIds = drainStreamIds,
    };

    var groupedWork = _reconcileAcknowledgementsAndPrepareWork(
      workBatch, sentCompletionCount: sentCompletionCount, sentFailureCount: sentFailureCount);

    _recordBatchMetrics(batchActivity, workBatch, groupedWork, [], []);
    _logBatchComposition(workBatch, groupedWork);
    _updateWorkStateTracking(workBatch.PerspectiveWork.Count > 0 || workBatch.PerspectiveStreamIds.Count > 0);

    var batchProcessedEvents = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();
    var batchIsNewByEventId = new ConcurrentDictionary<Guid, bool>();

    // Drain mode (mirrors legacy _processWorkBatchAsync behavior): when ClaimWorker
    // forwarded SQL-detected drain stream IDs, batch-fetch + RunWithEventsAsync them.
    // If drain processed nothing, fall through to the per-event path so events don't
    // stay claimed forever.
    if (workBatch.PerspectiveStreamIds.Count > 0) {
      await _processDrainModeStreamsAsync(
        scope, workBatch.PerspectiveStreamIds, batchProcessedEvents, batchIsNewByEventId,
        lifecycleCoordinator, cancellationToken).ConfigureAwait(false);
      if (!batchProcessedEvents.IsEmpty) {
        groupedWork = [];
      }
    }

    await Parallel.ForEachAsync(
      groupedWork,
      new ParallelOptions {
        MaxDegreeOfParallelism = _options.MaxConcurrentPerspectives,
        CancellationToken = cancellationToken
      },
      async (group, ct) => {
        var streamId = group.Key.StreamId;
        var perspectiveName = group.Key.PerspectiveName;
        await using var groupScope = _scopeFactory.CreateAsyncScope();
        var groupWorkCoordinator = groupScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
        var groupReceptorInvoker = groupScope.ServiceProvider.GetService<IReceptorInvoker>();
        var groupLifecycleCoordinator = groupScope.ServiceProvider.GetService<ILifecycleCoordinator>();

        var (checkpoint, runner, eventStore, upcomingEvents, perspectiveParentContext) =
          await _resolveDependenciesAndLoadEventsAsync(
            groupScope, groupWorkCoordinator, groupReceptorInvoker, streamId, perspectiveName,
            batchActivity, effectiveParent, ct);

        if (runner is null) {
          return;
        }

        var lastProcessedEventId = checkpoint?.LastEventId;
        var enablePerspectiveSpans = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Perspectives) ?? false;
        using var perspectiveActivity = enablePerspectiveSpans
          ? WhizbangActivitySource.Tracing.StartActivity(
              $"Perspective {perspectiveName}",
              ActivityKind.Internal,
              parentContext: perspectiveParentContext)
          : null;
        _tagPerspectiveActivity(perspectiveActivity, perspectiveName, streamId, upcomingEvents, perspectiveParentContext);

        var enableLifecycleSpans = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;
        var streamCtx = new PerspectiveStreamContext(streamId, perspectiveName, lastProcessedEventId, groupScope.ServiceProvider);

        try {
          await _invokePrePerspectiveLifecycleAsync(
            upcomingEvents, enableLifecycleSpans, groupLifecycleCoordinator, groupReceptorInvoker,
            streamCtx, runner, ct);

          var (result, processingMode, rewindLockSkipped) = await _executePerspectiveRunnerAsync(
            group, runner, checkpoint, streamCtx, enablePerspectiveSpans, ct);

          if (rewindLockSkipped) {
            return;
          }

          var processedEvents = await _loadAndLogProcessedEventsAsync(
            groupReceptorInvoker, eventStore, result, streamId, perspectiveName,
            lastProcessedEventId, ct);

          // Rewind path: the range-based load above covers events above the pre-rewind
          // cursor. Events below the cursor (the rewind trigger AND any other late arrivals
          // that accumulated during the rewind window) live in the perspective work queue —
          // IPerspectiveReplayReader is the authoritative source for that "is_new" set.
          // When registered, we use it to pull every pending event the rewind should fire
          // handlers for. When not registered, we fall back to the narrow trigger-only
          // lookup so existing deployments keep working.
          if (processingMode == ProcessingMode.Replay) {
            var replayReader = groupScope.ServiceProvider.GetService<Whizbang.Core.Perspectives.IPerspectiveReplayReader>();
            if (replayReader is not null && _eventTypeProvider is not null) {
              var eventTypes = _eventTypeProvider.GetEventTypes();
              var seen = processedEvents.Select(e => e.MessageId.Value).ToHashSet();
              await foreach (var annotated in replayReader.ReadReplayEventsAsync(
                  streamId, perspectiveName, fromVersionExclusive: 0, eventTypes, ct)) {
                if (annotated.IsNew && seen.Add(annotated.Envelope.MessageId.Value)) {
                  processedEvents.Insert(0, annotated.Envelope);
                }
              }
            } else if (checkpoint?.RewindTriggerEventId is { } triggerId
                       && eventStore is not null
                       && _eventTypeProvider is not null
                       && !processedEvents.Any(e => e.MessageId.Value == triggerId)) {
              var envelopesUpToTrigger = await eventStore.GetEventsBetweenPolymorphicAsync(
                streamId,
                afterEventId: null,
                upToEventId: triggerId,
                _eventTypeProvider.GetEventTypes(),
                ct);
              var triggerEnvelope = envelopesUpToTrigger
                .FirstOrDefault(e => e.MessageId.Value == triggerId);
              if (triggerEnvelope is not null) {
                processedEvents.Insert(0, triggerEnvelope);
              }
            }
          }

          foreach (var envelope in processedEvents) {
            var id = envelope.MessageId.Value;
            batchProcessedEvents.TryAdd(id, (envelope, streamId));
            batchIsNewByEventId.AddOrUpdate(id, true, (_, existing) => existing || true);
          }

          await _reportCompletionAndSignalSyncAsync(
            result, processedEvents, groupWorkCoordinator, streamId, perspectiveName, ct);
          await _invokePostPerspectiveLifecycleAsync(
            processedEvents, groupReceptorInvoker, streamCtx, result,
            new PostPerspectiveLifecycleOptions(enableLifecycleSpans, processingMode, IsNewByEventId: null), ct);
          _bufferCompletionsAndUpdateCache(group, processedEvents, groupLifecycleCoordinator, perspectiveName);

          if (processedEvents.Count > 0) {
            OnPerspectiveEventProcessed?.Invoke(new PerspectiveEventProcessedEvent {
              PerspectiveName = perspectiveName,
              StreamId = streamId,
              EventCount = processedEvents.Count
            });
          }
          _metrics?.StreamsUpdated.Add(1);
          if (processedEvents.Count > 0) {
            _metrics?.EventsProcessed.Add(processedEvents.Count);
          }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogErrorProcessingPerspectiveCursor(_logger, ex, perspectiveName, streamId);
          _metrics?.Errors.Add(1);
          if (_syncEventTracker is not null && upcomingEvents is { Count: > 0 }) {
            var failedEventIds = upcomingEvents.Select(e => e.MessageId.Value).ToList();
            _syncEventTracker.MarkProcessedByPerspective(failedEventIds, perspectiveName);
          }
          var failure = new PerspectiveCursorFailure {
            StreamId = streamId,
            PerspectiveName = perspectiveName,
            LastEventId = Guid.Empty,
            Status = PerspectiveProcessingStatus.Failed,
            Error = ex.Message
          };
          await _completionStrategy.ReportFailureAsync(failure, groupWorkCoordinator, ct);
          throw;
        }
      });

    if (!batchProcessedEvents.IsEmpty) {
      var priorPostLifecycle = Interlocked.Exchange(ref _pendingPostLifecycle, null);
      if (priorPostLifecycle is not null) {
        try {
          await priorPostLifecycle;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogPriorPostLifecycleFaulted(_logger, ex);
        }
      }
      var bgProcessedEvents = batchProcessedEvents;
      var bgGroupedWork = groupedWork;
      var bgIsNew = batchIsNewByEventId;
      var bgCoordinator = lifecycleCoordinator;
      var bgCt = cancellationToken;
      _pendingPostLifecycle = BackgroundStageDispatch.StartLongRunning(async () => {
        await using var bgScope = _scopeFactory.CreateAsyncScope();
        var bgReceptorInvoker = bgScope.ServiceProvider.GetService<IReceptorInvoker>();
        await _firePostLifecycleDetached(
          bgProcessedEvents, bgCoordinator, bgReceptorInvoker, bgGroupedWork,
          bgScope.ServiceProvider, bgCt, bgIsNew);
      }, cancellationToken);
    }

    _logBatchSummary([], [], workBatch);
    _metrics?.BatchesProcessed.Add(1);
    _metrics?.BatchDuration.Record(batchSw.Elapsed.TotalMilliseconds);
    if (_logger.IsEnabled(LogLevel.Debug)) {
      LogDrainCycleComplete(_logger, !_isIdle, batchProcessedEvents.Count, batchSw.ElapsedMilliseconds);
    }
    OnBatchCycleComplete?.Invoke();
  }

  /// <summary>
  /// Drains <see cref="_completionStrategy"/>'s pending queues + the perspective event completion
  /// queue, writes each item to the appropriate Phase C channel (fire-and-forget), and marks
  /// the strategy entries as "Sent". Returns counts for local acknowledgement.
  /// </summary>
  private async Task<(int CompletionCount, int FailureCount)> _flushPendingCompletionsToChannelsAsync(
    CancellationToken ct) {
    var pendingCompletions = _completionStrategy.GetPendingCompletions();
    var pendingFailures = _completionStrategy.GetPendingFailures();

    foreach (var tc in pendingCompletions) {
      await _perspectiveCompletionChannel!.EnqueueCursorAsync(tc.Completion, ct).ConfigureAwait(false);
    }
    foreach (var tc in pendingFailures) {
      var f = tc.Completion;
      await _failureChannel!.EnqueueAsync(WorkCategory.PerspectiveEvent, new MessageFailure {
        MessageId = f.LastEventId,
        CompletedStatus = MessageProcessingStatus.None,
        Error = f.Error ?? "perspective failed",
        Reason = MessageFailureReason.Unknown,
      }, ct).ConfigureAwait(false);
    }
    while (_pendingEventCompletions.TryDequeue(out var ec)) {
      await _perspectiveCompletionChannel!.EnqueueEventWorkIdAsync(ec.EventWorkId, ct).ConfigureAwait(false);
    }

    _completionStrategy.MarkAsSent(pendingCompletions, pendingFailures, DateTimeOffset.UtcNow);
    return (pendingCompletions.Length, pendingFailures.Length);
  }

  /// <summary>
  /// Drain mode: processes perspective events for leased streams via batch-fetch + RunWithEventsAsync.
  /// Single SQL round-trip for all events, pre-deserialized, perspectives run with pre-fetched events.
  /// Full lifecycle chain: PrePerspective → RunWithEvents → PostPerspective → signal coordinator.
  /// PostAllPerspectives + PostLifecycle fire via _firePostLifecycleDetached after this returns.
  /// </summary>
  /// <docs>fundamentals/perspectives/drain-mode</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDrainModeLifecycleTests.cs</tests>
  private async Task _processDrainModeStreamsAsync(
      AsyncServiceScope scope,
      List<Guid> streamIds,
      ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> batchProcessedEvents,
      ConcurrentDictionary<Guid, bool> batchIsNewByEventId,
      ILifecycleCoordinator? lifecycleCoordinator,
      CancellationToken cancellationToken) {
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    var fetchResult = await _tryFetchAndDeserializeDrainModeEventsAsync(
      scope, workCoordinator, streamIds, cancellationToken);
    if (fetchResult is null) {
      return;
    }
    var (typedEvents, rawByEventId) = fetchResult.Value;

    var typeNameCache = _buildDrainModeTypeNameCache(typedEvents);
    var eventsByStream = _groupAndDedupeDrainModeEventsByStream(typedEvents, rawByEventId);

    await _prefetchMissingDrainModeCursorsAsync(scope, eventsByStream.Keys, cancellationToken);

    var drainBatchContext = new DrainBatchContext(
      rawByEventId, typeNameCache, batchProcessedEvents, batchIsNewByEventId, lifecycleCoordinator);
    await Parallel.ForEachAsync(
      eventsByStream,
      new ParallelOptions {
        MaxDegreeOfParallelism = _options.MaxConcurrentPerspectives,
        CancellationToken = cancellationToken
      },
      (streamGroup, ct) => new ValueTask(_processDrainModeStreamAsync(
        scope, workCoordinator, streamGroup.Key, streamGroup.Value, drainBatchContext, ct)));
  }

  /// <summary>
  /// Drain-mode prerequisites + batch fetch + typed deserialization. Returns null whenever
  /// downstream processing should be skipped (missing dependencies, no rows, deserialize threw,
  /// or zero typed envelopes produced) so the caller can short-circuit cleanly.
  /// </summary>
  private async Task<(List<MessageEnvelope<IEvent>> TypedEvents, ILookup<Guid, StreamEventData> RawByEventId)?> _tryFetchAndDeserializeDrainModeEventsAsync(
      AsyncServiceScope scope,
      IWorkCoordinator workCoordinator,
      List<Guid> streamIds,
      CancellationToken cancellationToken) {
    var eventStore = scope.ServiceProvider.GetService<IEventStore>();

    if (eventStore is null || _eventTypeProvider is null || _perspectivesPerEventType is null) {
#pragma warning disable CA1848
      _logger.LogWarning("Drain mode skipped: EventStore={HasStore}, EventTypeProvider={HasProvider}, PerspectiveMap={HasMap}",
        eventStore is not null, _eventTypeProvider is not null, _perspectivesPerEventType is not null);
#pragma warning restore CA1848
      return null;
    }

    // Batch-fetch all events for leased streams in a single SQL call.
    var rawEvents = await workCoordinator.GetStreamEventsAsync(
      _instanceProvider.InstanceId, [.. streamIds], cancellationToken);

    if (rawEvents.Count == 0) {
      return null;
    }

    // Deserialize raw events into typed envelopes (AOT-safe).
    var eventTypes = _eventTypeProvider.GetEventTypes();
    List<MessageEnvelope<IEvent>> typedEvents;
    try {
      typedEvents = eventStore.DeserializeStreamEvents(rawEvents, eventTypes);
    } catch (Exception ex) {
#pragma warning disable CA1848
      _logger.LogError(ex, "Drain mode: DeserializeStreamEvents threw {ExceptionType}: {Message}", ex.GetType().Name, ex.Message);
#pragma warning restore CA1848
      return null;
    }

    if (typedEvents.Count == 0) {
#pragma warning disable CA1848
      _logger.LogWarning("Drain mode: DeserializeStreamEvents returned 0 typed events from {RawCount} raw events", rawEvents.Count);
#pragma warning restore CA1848
      return null;
    }

    // Build a lookup from raw event ID → StreamEventData list for work IDs (needed for completion).
    // The same event can appear multiple times when multiple perspectives reference it
    // (get_stream_events joins perspective_events × event_store). Each row has a unique EventWorkId
    // that must be completed individually.
    return (typedEvents, rawEvents.ToLookup(r => r.EventId));
  }

  /// <summary>Avoids N×M TypeNameFormatter.Format allocations inside the per-stream filter loops.</summary>
  private static Dictionary<Type, string> _buildDrainModeTypeNameCache(List<MessageEnvelope<IEvent>> typedEvents) {
    var typeNameCache = new Dictionary<Type, string>();
    foreach (var envelope in typedEvents) {
      var type = envelope.Payload.GetType();
      typeNameCache.TryAdd(type, TypeNameFormatter.Format(type));
    }
    return typeNameCache;
  }

  /// <summary>
  /// Groups typed events by streamId and dedupes by MessageId.
  /// get_stream_events joins perspective_events × event_store so the same event can appear
  /// multiple times (one row per queued perspective_events entry). Feeding those duplicates
  /// downstream causes the generated runner's ApplyEvent to fire once per envelope, which is
  /// where the Apply-exactly-once contract breaks. DistinctBy(MessageId) collapses duplicates
  /// before any Apply dispatch or lifecycle-coordinator tracking. Raw rows are preserved in
  /// rawByEventId so every queued EventWorkId still receives its own completion write.
  /// </summary>
  internal static Dictionary<Guid, List<MessageEnvelope<IEvent>>> _groupAndDedupeDrainModeEventsByStream(
      List<MessageEnvelope<IEvent>> typedEvents,
      ILookup<Guid, StreamEventData> rawByEventId) {
    return typedEvents
      .GroupBy(e => rawByEventId[e.MessageId.Value].First().StreamId)
      .ToDictionary(
        g => g.Key,
        g => g.DistinctBy(e => e.MessageId.Value).OrderByMessageId().ToList());
  }

  /// <summary>Batch-fetches cursors for any streams not already in the local cache.</summary>
  private async Task _prefetchMissingDrainModeCursorsAsync(
      AsyncServiceScope scope,
      IEnumerable<Guid> streamIds,
      CancellationToken cancellationToken) {
    var cursorsNotCached = streamIds.Where(sid => !_cursorCache.HasStream(sid)).ToArray();
    if (cursorsNotCached.Length == 0) {
      return;
    }
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var batchCursors = await workCoordinator.GetPerspectiveCursorsBatchAsync(cursorsNotCached, cancellationToken);
    foreach (var cursor in batchCursors) {
      _hydrateCursorCacheEntry(_cursorCache, cursor);
    }
  }

  /// <summary>
  /// Slice 26.13 — hydrates both halves of the cursor cache from a single
  /// <see cref="PerspectiveCursorInfo"/>. Without warming the commit_sequence half on
  /// cold caches (process start, post-rewind invalidate, eviction), the inversion detector
  /// falls back to event_id (UUIDv7 lex) comparison and re-introduces same-millisecond
  /// commit-order false positives. Extracted as <c>internal static</c> so the wiring is
  /// directly unit-testable without spinning up a worker.
  /// </summary>
  internal static void _hydrateCursorCacheEntry(PerspectiveCursorCache cache, PerspectiveCursorInfo cursor) {
    if (cursor.LastEventId.HasValue) {
      cache.Set(cursor.StreamId, cursor.PerspectiveName, cursor.LastEventId.Value);
    }
    if (cursor.LastCommitSequence.HasValue) {
      cache.SetCommitSequence(cursor.StreamId, cursor.PerspectiveName, cursor.LastCommitSequence.Value);
    }
  }

  /// <summary>
  /// Processes all perspectives for a single leased stream. Runs registered lifecycle tracking
  /// for the WhenAll PostAllPerspectives gate, then executes each applicable perspective runner
  /// in its own DI scope.
  /// </summary>
  private async Task _processDrainModeStreamAsync(
      AsyncServiceScope scope,
      IWorkCoordinator workCoordinator,
      Guid streamId,
      List<MessageEnvelope<IEvent>> streamEvents,
      DrainBatchContext batchContext,
      CancellationToken ct) {
    // scope + workCoordinator come from the parent _processDrainModeStreamsAsync; they were
    // used SAFELY there because the initial fetch happens BEFORE Parallel.ForEachAsync fans
    // out. Inside this method we run concurrently with up to MaxConcurrentPerspectives peers
    // — sharing the parent DbContext for refetch would produce
    // NpgsqlOperationInProgressException. The refetch helper creates its own scope per call.
    _ = scope;
    _ = workCoordinator;
    // Phase H step 6 slice 5: bracket the entire per-stream drain with the channel-level
    // in-flight marker so ClaimWorker's _distributeAsync skips re-emitting this stream while
    // we're still working on it. Symmetric with OutboxDrainWorker / InboxDrainWorker Part B.
    _perspectiveDrainChannel?.MarkDraining(streamId);
    try {
      // Slice 30: loop-until-empty inside the drain. JDX run 21 PERF data showed 22,318 single-
      // event drains × ~150 ms each = ~55 min of per-drain envelope overhead. The dominant cost
      // per drain (DI scope + LeaseHandle + BackgroundStageDispatch OS thread spawn + commit
      // strategy plumbing) is roughly the same whether we process 1 or 44 events. By refetching
      // pending events for THIS stream after the first pass — events that arrived from the
      // transport DURING this drain or that the prior fetch missed due to claim timing — we
      // amortize the envelope across all events for the stream in the same call.
      //
      // Termination: each iteration either processes ≥1 fresh event (cooldown filters
      // already-processed) or refetches empty. Iteration cap (DrainLoopMaxIterations) prevents
      // pathological scenarios where the transport feeds faster than apply can keep up. When
      // the cap is hit, ClaimWorker picks up the stream on the next tick and we drain again
      // with a fresh envelope — same throughput as today, no regression.
      var iter = 0;
      var currentEvents = streamEvents;
      var currentContext = batchContext;
      while (iter < _options.DrainLoopMaxIterations) {
        iter++;
        var perspectiveNames = _collectDrainModePerspectiveNames(currentEvents, currentContext.TypeNameCache);
        _registerDrainModeLifecycleTracking(currentEvents, currentContext.TypeNameCache, streamId, currentContext.LifecycleCoordinator);

        foreach (var perspectiveName in perspectiveNames) {
          var filteredEvents = currentEvents
              .Where(e => currentContext.TypeNameCache.TryGetValue(e.Payload.GetType(), out var key)
                && _perspectivesPerEventType!.TryGetValue(key, out var ps) && ps.Contains(perspectiveName))
              .OrderByMessageId()
              .ToList();

          if (filteredEvents.Count == 0) {
            continue;
          }
          // Slice 30 defensive: an OCE bubbling out of the drain-mode perspective method
          // — for example shutdown cancellation with both ct + lease.Token cancelled so its
          // own catch filter does not match — must not abort the loop for other perspectives
          // on this stream. The single perspective's lease-handle catch already routes
          // failure-mode OCEs; anything that bubbles is either expected shutdown propagation
          // or an unhandled edge case, neither of which should poison sibling perspectives.
          try {
            await _runDrainModePerspectiveAsync(
              streamId, perspectiveName, filteredEvents, currentContext, ct);
          } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Worker shutdown — propagate out of the loop, Parallel.ForEachAsync handles it.
            throw;
          } catch (OperationCanceledException ex) {
            // Lease-handle OCE that wasn't caught by _runDrainModePerspectiveAsync's filter.
            // Log + continue with next perspective; the row's lease will expire and
            // claim_orphaned will re-issue.
#pragma warning disable CA1848
            _logger.LogWarning(ex, "Drain mode: unhandled OCE from {Perspective} on stream {StreamId} — skipping to next perspective", perspectiveName, streamId);
#pragma warning restore CA1848
          }
        }

        // Slice 30 — refetch gate. Skip the refetch SQL roundtrip when this iteration only
        // processed a single event: that's the steady-state low-arrival-rate case where a
        // burst arrival during the drain is unlikely, and an empty refetch costs ~5-10 ms of
        // wasted SQL. Multi-event iterations (bursts) are exactly the case where MORE events
        // are likely still arriving — refetch is the right bet there.
        if (currentEvents.Count < _options.DrainLoopRefetchMinBatch) {
          break;
        }
        // Defensive: a refetch failure (transient DB error, lease cancellation, etc.) MUST
        // NOT abort the already-completed first-iteration processing. The first iteration's
        // work + completion enqueue have already landed; if refetch fails, just exit the loop
        // and let the next ClaimWorker tick re-issue the stream if work is still pending.
        (List<MessageEnvelope<IEvent>> Events, DrainBatchContext Context)? refetched;
        try {
          refetched = await _tryRefetchDrainModeStreamEventsAsync(streamId, currentContext, ct);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
          break;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
#pragma warning disable CA1848
          _logger.LogWarning(ex, "Drain mode: refetch threw for stream {StreamId} — exiting loop, ClaimWorker will re-issue", streamId);
#pragma warning restore CA1848
          break;
        }
        if (refetched is null) {
          break;
        }
        var (refetchedEvents, refetchedContext) = refetched.Value;
        currentEvents = refetchedEvents;
        currentContext = refetchedContext;
      }
    } finally {
      _perspectiveDrainChannel?.MarkDrained(streamId);
    }
  }

  /// <summary>
  /// Slice 30 — refetches pending perspective events for one stream after an in-progress drain
  /// has processed its initial batch. Returns null when no fresh events came back (loop exits).
  /// Returns a typed-event list + a DrainBatchContext keyed to the refetched rows when there are
  /// still events to process. Reuses cross-stream shared accumulators (BatchProcessedEvents,
  /// BatchIsNewByEventId, LifecycleCoordinator) so PostAllPerspectives still fires correctly.
  /// Already-applied events come back too (until completion-flush DELETEs them); the cooldown
  /// cache short-circuits them inside _runDrainModePerspectiveAsync so we don't re-apply.
  /// </summary>
  private async Task<(List<MessageEnvelope<IEvent>> Events, DrainBatchContext Context)?> _tryRefetchDrainModeStreamEventsAsync(
      Guid streamId,
      DrainBatchContext sharedContext,
      CancellationToken ct) {
    // Create a fresh scope per refetch — the parent _processDrainModeStreamsAsync's scope
    // hosts the initial workCoordinator that handed events to ALL parallel streams; if N
    // streams' refetches share it we get Npgsql command-already-in-progress on the shared
    // DbContext. Each refetch gets an isolated DbContext / connection.
    await using var refetchScope = _scopeFactory.CreateAsyncScope();
    var refetchWorkCoordinator = refetchScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var fetchResult = await _tryFetchAndDeserializeDrainModeEventsAsync(
      refetchScope, refetchWorkCoordinator, [streamId], ct);
    if (fetchResult is null) {
      return null;
    }
    var (typedEvents, rawByEventId) = fetchResult.Value;
    if (typedEvents.Count == 0) {
      return null;
    }
    var typeNameCache = _buildDrainModeTypeNameCache(typedEvents);
    var grouped = _groupAndDedupeDrainModeEventsByStream(typedEvents, rawByEventId);
    if (!grouped.TryGetValue(streamId, out var eventsForStream) || eventsForStream.Count == 0) {
      return null;
    }
    var nextContext = new DrainBatchContext(
      rawByEventId,
      typeNameCache,
      sharedContext.BatchProcessedEvents,
      sharedContext.BatchIsNewByEventId,
      sharedContext.LifecycleCoordinator);
    return (eventsForStream, nextContext);
  }

  /// <summary>Collects the set of perspective names that apply to the event types in this stream.</summary>
  private HashSet<string> _collectDrainModePerspectiveNames(
      List<MessageEnvelope<IEvent>> streamEvents,
      Dictionary<Type, string> typeNameCache) {
    var perspectiveNames = new HashSet<string>();
    foreach (var envelope in streamEvents) {
      if (typeNameCache.TryGetValue(envelope.Payload.GetType(), out var eventTypeKey)
          && _perspectivesPerEventType!.TryGetValue(eventTypeKey, out var perspectives)) {
        foreach (var p in perspectives) {
          perspectiveNames.Add(p);
        }
      }
    }
    return perspectiveNames;
  }

  /// <summary>
  /// Coordinator tracking + ExpectPerspectiveCompletions set up the WhenAll gate that fires
  /// PostAllPerspectives once every perspective has completed.
  /// </summary>
  /// <remarks>
  /// PrePerspective receptor invocation is intentionally NOT done here. The generated
  /// IPerspectiveRunner (RunWithEventsAsync) fires PrePerspectiveDetached + PrePerspectiveInline
  /// itself at the start of its processing loop (see PerspectiveRunnerTemplate.cs). Firing them
  /// here too would double-invoke every registered Pre* receptor.
  /// </remarks>
  private void _registerDrainModeLifecycleTracking(
      List<MessageEnvelope<IEvent>> streamEvents,
      Dictionary<Type, string> typeNameCache,
      Guid streamId,
      ILifecycleCoordinator? lifecycleCoordinator) {
    if (lifecycleCoordinator is null) {
      return;
    }
    foreach (var envelope in streamEvents) {
      _ = lifecycleCoordinator.BeginTracking(
        envelope.MessageId.Value, envelope,
        LifecycleStage.PrePerspectiveDetached, MessageSource.Local, streamId);

      if (typeNameCache.TryGetValue(envelope.Payload.GetType(), out var eventTypeKey)
          && _perspectivesPerEventType!.TryGetValue(eventTypeKey, out var expected)) {
        lifecycleCoordinator.ExpectPerspectiveCompletions(envelope.MessageId.Value, expected);
      }
    }
  }

  /// <summary>
  /// Runs a single perspective's RunWithEventsAsync for a stream, reports completion/failure,
  /// and flushes the supporting trackers/signalers/metrics. Keeps the pre-report and post-report
  /// completed-only blocks separate to preserve the observable ordering of the original monolith.
  /// </summary>
  private async Task _runDrainModePerspectiveAsync(
      Guid streamId,
      string perspectiveName,
      List<MessageEnvelope<IEvent>> filteredEvents,
      DrainBatchContext batchContext,
      CancellationToken ct) {
    await using var groupScope = _scopeFactory.CreateAsyncScope();
    var groupWorkCoordinator = groupScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var registry = groupScope.ServiceProvider.GetService<IPerspectiveRunnerRegistry>();
    var runner = registry?.GetRunner(perspectiveName, groupScope.ServiceProvider);

    if (runner is null) {
      return;
    }

    Guid? lastProcessedEventId = null;
    if (_cursorCache.TryGet(streamId, perspectiveName, out var cachedEventId)) {
      lastProcessedEventId = cachedEventId;
    }

    // Slice 26.10: prefer commit_sequence comparison for inversion detection when both
    // the cached cursor and the incoming events have commit_sequence stamped. This is
    // the architectural fix for the JDX run-8 commit-order race — stamped values reflect
    // commit-completion order (stable, never re-orders), unlike UUIDv7 event_ids which
    // are stamped at generation-time and can invert under concurrent saga writers.
    // Fall through to event_id comparison when either side lacks commit_sequence (stamper
    // hasn't caught up, or row pre-dates slice 26).
    long? lastProcessedCommitSequence = null;
    if (_cursorCache.TryGetCommitSequence(streamId, perspectiveName, out var cachedSeq)) {
      lastProcessedCommitSequence = cachedSeq;
    }

    // Slice 26.15: partition cooled vs fresh BEFORE the inversion check. The cursor-flush
    // race between drain finish and PerspectiveCompletionFlushWorker DELETE produces a
    // window where just-applied event_work_ids are still in DB pending. Pre-26.15 the
    // inversion detector ran on the full set, saw those cooled events as "pending ≤ cursor",
    // and triggered a spurious rewind — measured as ~1100 phantom rewinds per JDX bulk-import
    // when sagas commit N events in two close transactions (drainer re-fetches between flush
    // ticks, sees first-batch events still warm, second-batch fresh, mixed bag).
    var (cooledEvents, freshEvents) = _partitionByCooldown(
      filteredEvents, batchContext.RawByEventId, _recentlyProcessedEventCache, perspectiveName);

    // Signal cooled events into the batch's completion bookkeeping so PostAllPerspectives
    // can fire correctly — same JDX 2026-05-03 reasoning as the all-cooled short-circuit.
    // SignalPerspectiveComplete + BatchProcessedEvents.TryAdd are idempotent so re-signaling
    // is safe even when the previous drain already signaled them.
    if (cooledEvents.Count > 0) {
      _signalCooldownSkippedEvents(
        cooledEvents, perspectiveName, streamId,
        batchContext.BatchProcessedEvents, batchContext.LifecycleCoordinator);
    }

    // Everything cooled → previous drain handled it; nothing left to do.
    if (freshEvents.Count == 0) {
      return;
    }

    // Phase H step 6 slice 4: cursor-inversion detector — now scoped to the fresh remainder
    // so cursor-flush-race events don't masquerade as real inversions.
    Guid? inversionAnchor = _resolveInversionAnchor(
      freshEvents, batchContext.RawByEventId, lastProcessedEventId, lastProcessedCommitSequence);

    // freshEvents is what we'll apply (or rewind around).
    filteredEvents = freshEvents;

    // Phase H step 9 slice 4: lease-tied cancellation. Wrap the apply + post-apply housekeeping
    // in a LeaseHandle whose token cancels at lease_expiry - LeaseGraceSeconds. The runner
    // template's apply loop now also calls ThrowIfCancellationRequested between events so a
    // hot stream with many pending events can be cancelled mid-batch. The DispatchExecutor
    // abandons hung receptors that ignore the CT.
    var leaseDeadline = _timeProvider.GetUtcNow()
      + TimeSpan.FromSeconds(Math.Max(1, _leaseRenewalOptions.LeaseSeconds - _leaseHandleOptions.LeaseGraceSeconds));
    using var lease = new LeaseHandle(
      workId: streamId,
      category: WorkCategory.PerspectiveEvent,
      deadline: leaseDeadline,
      maxRenewals: _leaseHandleOptions.MaxRenewalsPerWork,
      timeProvider: _timeProvider,
      linkedTokens: [ct]);

    var drainStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    try {
      await LeaseDispatchExecutor.RunWithLeaseAsync(lease, async leaseCt => {
        PerspectiveCursorCompletion result;
        if (inversionAnchor.HasValue) {
          // Cache is stale — reset and let RewindAndRunAsync rebuild from snapshot/event-zero.
          _cursorCache.Invalidate(streamId, perspectiveName);
          // Slice 26.11: look up the violator's commit_sequence so RewindAndRunAsync can
          // use the commit-sequence-anchored snapshot path for full determinism. Falls
          // back to event_id-anchored snapshot when the violator wasn't stamped yet.
          long? anchorCommitSequence = batchContext.RawByEventId[inversionAnchor.Value]
            .Select(raw => raw.CommitSequence)
            .FirstOrDefault(seq => seq.HasValue);
          // Slice 26.16 instrumentation: emit detailed inversion diagnostics so we can
          // classify the residual ~400 inversions per JDX import. Captures whether the
          // commit_sequence detector or the event_id fallback fired, the gap, and the
          // partition counts so we can spot cooldown misses.
          long? pendingSeq = anchorCommitSequence;
          LogInversionDiagnostics(
            _logger,
            streamId,
            perspectiveName,
            inversionAnchor.Value,
            lastProcessedEventId ?? Guid.Empty,
            pendingSeq ?? -1,
            lastProcessedCommitSequence ?? -1,
            cooledEvents.Count,
            freshEvents.Count);
          LogRewindTriggered(_logger, streamId, perspectiveName, inversionAnchor.Value, lastProcessedEventId ?? Guid.Empty);
          result = await runner.RewindAndRunAsync(streamId, perspectiveName, inversionAnchor.Value, anchorCommitSequence, leaseCt);
        } else {
          result = await runner.RunWithEventsAsync(
            streamId, perspectiveName, lastProcessedEventId, filteredEvents, leaseCt);
        }

        // Slice 29 instrumentation: capture per-drain wall time partitioned into the three
        // dominant phases — runner (read + apply + save), completion (cursor update + lifecycle),
        // completion-enqueue + signaler. Surfaces the per-drain cost dominant when bff drains
        // at ~40 events/sec despite 4×30 concurrency configured.
        var runnerEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var runnerMs = (runnerEndTicks - drainStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        if (result.Status == PerspectiveProcessingStatus.Completed) {
          var streamCtx = new PerspectiveStreamContext(streamId, perspectiveName, lastProcessedEventId, groupScope.ServiceProvider);
          await _applyDrainModePerspectiveCompletionAsync(
            streamCtx, filteredEvents, result, batchContext, leaseCt);
        }
        var completionMs = (System.Diagnostics.Stopwatch.GetTimestamp() - runnerEndTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        await _completionStrategy.ReportCompletionAsync(result, groupWorkCoordinator, leaseCt);

        if (filteredEvents.Count > 0 && _syncEventTracker is not null) {
          var processedEventIds = filteredEvents.Select(e => e.MessageId.Value).ToList();
          _syncEventTracker.MarkProcessedByPerspective(processedEventIds, perspectiveName);
        }

        if (result.PerspectiveType is not null) {
          _syncSignaler?.SignalCheckpointUpdated(result.PerspectiveType, streamId, result.LastEventId);
        }

        if (result.Status == PerspectiveProcessingStatus.Completed) {
          _enqueueDrainModePerspectiveCompletions(streamId, perspectiveName, filteredEvents, batchContext.RawByEventId);
          // Slice 26.17: cooldown is now marked at the top of _applyDrainModePerspectiveCompletionAsync,
          // before any cursor cache update or lifecycle invocation that could throw. The
          // late-mark site here was the source of the residual UberDraftJob inversions in JDX
          // run 16 — keep the call site comment as a regression breadcrumb.
        }

        _metrics?.StreamsUpdated.Add(1);
        if (filteredEvents.Count > 0) {
          _metrics?.EventsProcessed.Add(filteredEvents.Count);
        }

        // Slice 29 instrumentation: emit per-drain perf breakdown at Debug when we processed
        // >= 5 events or the drain took more than 100ms. Surfaces whether per-drain time
        // scales with event count (apply is the cost) or is fixed-per-call (lifecycle / DI
        // scope is the cost). Enable Debug logging for the worker to see these.
        if (_logger.IsEnabled(LogLevel.Debug)) {
          var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - drainStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
          if (filteredEvents.Count >= 5 || totalMs > 100) {
#pragma warning disable CA1848
            _logger.LogDebug(
              "PERF Drain {Perspective} stream {StreamId}: events={EventCount} total={TotalMs:F0}ms runner={RunnerMs:F0}ms completion={CompletionMs:F0}ms cooled={Cooled}",
              perspectiveName, streamId, filteredEvents.Count, totalMs, runnerMs, completionMs, cooledEvents.Count);
#pragma warning restore CA1848
          }
        }
      });
    } catch (OperationCanceledException) when (lease.Token.IsCancellationRequested && !ct.IsCancellationRequested) {
      // Lease deadline fired (not worker shutdown). Route to failure path same as any other
      // exception so the row's lease releases and claim_orphaned re-issues with bumped attempts.
#pragma warning disable CA1848
      _logger.LogWarning("Drain mode: lease deadline exceeded for {Perspective} stream {StreamId} — routing to failure", perspectiveName, streamId);
#pragma warning restore CA1848
      _metrics?.Errors.Add(1);
      var failure = new PerspectiveCursorFailure {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.Empty,
        Status = PerspectiveProcessingStatus.Failed,
        Error = "Lease deadline exceeded — handler did not complete within the configured lease window."
      };
      await _completionStrategy.ReportFailureAsync(failure, groupWorkCoordinator, ct);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
#pragma warning disable CA1848
      _logger.LogError(ex, "Drain mode: Error processing perspective {Perspective} for stream {StreamId}", perspectiveName, streamId);
#pragma warning restore CA1848
      _metrics?.Errors.Add(1);

      var failure = new PerspectiveCursorFailure {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.Empty,
        Status = PerspectiveProcessingStatus.Failed,
        Error = ex.Message
      };
      await _completionStrategy.ReportFailureAsync(failure, groupWorkCoordinator, ct);
    }
  }

  /// <summary>
  /// Cursor-inversion detector. Returns the earliest event_id in <paramref name="events"/>
  /// that is strictly less than <paramref name="cachedCursor"/>, or <c>null</c> if no inversion
  /// exists. UUIDv7 is lexicographic-and-time-ordered, so the canonical "D" string compare
  /// matches the time order — same comparison the runner template uses for its idempotency filter.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Slice 4 — Phase H step 6. Inversion means a pending event's event_id is older than where
  /// the cursor is — the perspective row's metadata advanced past an event that's still in the
  /// pending queue. The strict invariant model (decision (a) in the design) treats this as
  /// "model state is wrong, replay from before the violator". The earliest violator is the
  /// rewind anchor — picking any later one risks a snapshot already past one of the
  /// violating events.
  /// </para>
  /// <para>
  /// <strong>Equal cursor is NOT inversion.</strong> When <c>event_id == cachedCursor</c>, the
  /// runner just applied that event (cursor advanced synchronously) but the
  /// <c>wh_perspective_events</c> row's <c>processed_at</c> hasn't landed yet (the completion
  /// flusher coalesces ~10 ms before writing). That's the expected state during the
  /// cursor-flush window — the runner's idempotency filter inside RunWithEventsAsync skips
  /// the duplicate. Triggering rewind here was a slice 4 over-trigger that produced hot-loop
  /// full replays in JDX BFF on hot session streams (observed 2026-05-02 ~09:43).
  /// </para>
  /// </remarks>
  internal static Guid? _findCursorInversionAnchor(
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      Guid cachedCursor) {
    if (cachedCursor == Guid.Empty) {
      return null;
    }
    var cursorStr = cachedCursor.ToString("D");
    Guid? earliest = null;
    foreach (var envelope in events) {
      var msgId = envelope.MessageId.Value;
      if (string.Compare(msgId.ToString("D"), cursorStr, StringComparison.Ordinal) < 0) {
        if (earliest is null
            || string.Compare(msgId.ToString("D"), earliest.Value.ToString("D"), StringComparison.Ordinal) < 0) {
          earliest = msgId;
        }
      }
    }
    return earliest;
  }

  /// <summary>
  /// Slice 26.13 — routes inversion detection to the authoritative detector based on what
  /// the cursor cache has. When <paramref name="lastProcessedCommitSequence"/> is set, the
  /// commit-sequence detector is the FINAL word — a null result from it means "no inversion,"
  /// not "I don't know," so we don't fall through to the event_id detector and re-introduce
  /// UUIDv7 same-millisecond false positives. The event_id detector is only used when
  /// commit_sequence cursor is unavailable (pre-slice-26 row, or cursor advanced to an
  /// unstamped event before slice 26.13 shipped).
  ///
  /// <para>
  /// Slice 26.18 — when commit_sequence cursor is missing but <em>any</em> pending event
  /// has a commit_sequence stamp, suppress the event_id fallback entirely. Mixing a
  /// stamped pending event against an unstamped cursor is meaningless and produced the
  /// dominant residual JDX run-18 inversions (6 of 8 logged cursorSeq=-1). The runner
  /// template's idempotency filter already drops events with event_id ≤ apply cursor at
  /// apply time, so biasing toward "no rewind" here is safe — the worst case is one
  /// extra applied-then-skipped pass, never a stale cursor.
  /// </para>
  /// </summary>
  internal static Guid? _resolveInversionAnchor(
      IReadOnlyList<MessageEnvelope<IEvent>> filteredEvents,
      ILookup<Guid, StreamEventData> rawByEventId,
      Guid? lastProcessedEventId,
      long? lastProcessedCommitSequence) {
    if (lastProcessedCommitSequence.HasValue) {
      return _findCursorInversionAnchorByCommitSequence(
        filteredEvents, rawByEventId, lastProcessedCommitSequence.Value);
    }
    if (lastProcessedEventId.HasValue) {
      // Slice 26.18 — if any pending event is stamped, we can't reliably compare an
      // unstamped event_id cursor against a stamped commit_sequence world. Skip detection.
      foreach (var envelope in filteredEvents) {
        foreach (var raw in rawByEventId[envelope.MessageId.Value]) {
          if (raw.CommitSequence.HasValue) {
            return null;
          }
        }
      }
      return _findCursorInversionAnchor(filteredEvents, lastProcessedEventId.Value);
    }
    return null;
  }

  /// <summary>
  /// Slice 26 — commit-sequence-based inversion anchor. Compares each event's
  /// <see cref="StreamEventData.CommitSequence"/> against the cached commit_sequence cursor.
  /// Returns the event_id of the earliest violator (commit_sequence ≤ cached) for snapshot-aware
  /// rewind. Events without a CommitSequence (stamper hasn't caught up) are SKIPPED — they're
  /// not yet stable for cursor comparison; if the caller has a commit_sequence cursor, that's
  /// authoritative (an unstamped event is newer than any stamped cursor by construction).
  /// </summary>
  internal static Guid? _findCursorInversionAnchorByCommitSequence(
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      ILookup<Guid, StreamEventData> rawByEventId,
      long cachedCommitSequence) {
    Guid? earliestEventId = null;
    long earliestSeq = long.MaxValue;
    foreach (var envelope in events) {
      var msgId = envelope.MessageId.Value;
      var raw = rawByEventId[msgId].FirstOrDefault();
      if (raw?.CommitSequence is null) {
        continue;  // Unstamped — defer to event_id comparison path.
      }
      var seq = raw.CommitSequence.Value;
      // Strict < — equality means the pending event IS the cursor's last-applied event
      // (cursor-flush race: row applied but perspective_events delete still in flight).
      // That's an idempotent re-drain, not an inversion. The runner template's idempotency
      // filter handles it without a rewind.
      if (seq < cachedCommitSequence && seq < earliestSeq) {
        earliestSeq = seq;
        earliestEventId = msgId;
      }
    }
    return earliestEventId;
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Cursor inversion detected: pending event {AnchorEventId} ≤ cached cursor {CachedCursor} for stream {StreamId} / perspective {PerspectiveName}; triggering rewind")]
  private static partial void LogRewindTriggered(ILogger logger, Guid streamId, string perspectiveName, Guid anchorEventId, Guid cachedCursor);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Inversion diagnostics: stream={StreamId} perspective={PerspectiveName} anchor={AnchorEventId} cursorEventId={CachedCursor} pendingSeq={PendingSeq} cursorSeq={CursorSeq} cooled={CooledCount} fresh={FreshCount}")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage source generator needs one parameter per placeholder; the structure mirrors the log template.")]
  private static partial void LogInversionDiagnostics(
    ILogger logger,
    Guid streamId,
    string perspectiveName,
    Guid anchorEventId,
    Guid cachedCursor,
    long pendingSeq,
    long cursorSeq,
    int cooledCount,
    int freshCount);

  /// <summary>
  /// Phase H step 7 slice 5: returns <c>true</c> when every event_work_id for the given filtered
  /// events is in the cooldown cache (recently processed within TTL). Drainer skips
  /// <c>RunWithEventsAsync</c> entirely in that case — the previous drain handled these events;
  /// the completion flush is in flight. Returns <c>false</c> when the cache is null (cooldown
  /// disabled), no events, or any work_id is fresh.
  /// </summary>
  /// <summary>
  /// When the cooldown gate skips apply (because a prior drain already applied these events
  /// within the cache TTL), this helper still asserts the lifecycle bookkeeping the apply
  /// success path normally would: it adds each filtered event to the current batch's
  /// <c>BatchProcessedEvents</c> dictionary and re-signals perspective completion on the
  /// lifecycle coordinator. Without this, cooldown-skipped events never satisfy
  /// <c>AreAllPerspectivesComplete</c> in the current batch, so the foreach in
  /// <c>_processBatchFinalizationAsync</c> never fires <c>PostAllPerspectivesInline</c> /
  /// <c>PostLifecycleInline</c> — and any <c>[FireAt(PostAllPerspectivesInline)]</c> receptor
  /// (e.g., saga completion dispatchers) never runs.
  /// </summary>
  /// <remarks>
  /// Both operations are idempotent under repeated calls: TryAdd no-ops on existing keys, and
  /// LifecycleCoordinator.SignalPerspectiveComplete uses a HashSet-style state per perspective
  /// name. Safe to call across batches or within the same batch.
  /// </remarks>
  internal static void _signalCooldownSkippedEvents(
      IReadOnlyList<MessageEnvelope<IEvent>> filteredEvents,
      string perspectiveName,
      Guid streamId,
      ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> batchProcessedEvents,
      ILifecycleCoordinator? lifecycleCoordinator) {
    foreach (var envelope in filteredEvents) {
      batchProcessedEvents.TryAdd(envelope.MessageId.Value, (envelope, streamId));
      lifecycleCoordinator?.SignalPerspectiveComplete(envelope.MessageId.Value, perspectiveName);
    }
  }

  /// <summary>
  /// Returns true when EVERY event_work_id for the current perspective in the filtered batch
  /// is in the cooldown cache — meaning a previous drain already applied them and we should
  /// skip Apply.
  /// </summary>
  /// <param name="perspectiveName">The current perspective being drained. The decision MUST
  /// only consider work_ids belonging to THIS perspective. JDX 2026-05-04 silent-skip bug:
  /// without filtering, marking UberDraftJob's work_id as cooled would also stop DraftJobSkills'
  /// Apply from running (since both perspectives share the same EventId in <paramref name="rawByEventId"/>
  /// but have distinct work_ids). When <paramref name="perspectiveName"/> is null (legacy
  /// callers), all entries for the EventId are considered — back-compat mode.</param>
  /// <summary>
  /// Slice 26.15 — partitions <paramref name="filteredEvents"/> into (cooled, fresh).
  /// "Cooled" events are those whose event_work_id is in <paramref name="cache"/> (just
  /// applied, perspective_events row not yet DELETEd by the flusher). "Fresh" events are
  /// the remainder. Drain path runs the inversion detector on the fresh remainder only —
  /// without this, the saga-batch race (44 events committed in two transactions 18ms
  /// apart, drainer re-fetches between completion-flush ticks) made the cooled events look
  /// like real inversions and produced ~1100 spurious rewinds per JDX bulk-import.
  ///
  /// <para>
  /// An envelope is treated as <strong>fresh</strong> when (a) <paramref name="cache"/> is
  /// null, (b) <paramref name="rawByEventId"/> has no row for it (mapping mismatch — same
  /// defensive default as <see cref="_shouldSkipApplyDueToCooldown"/>), or (c) any of its
  /// raw rows under the current perspective is NOT in the cache. An envelope is
  /// <strong>cooled</strong> only when at least one matching raw row exists and ALL such
  /// rows are cached.
  /// </para>
  /// </summary>
  internal static (List<MessageEnvelope<IEvent>> cooled, List<MessageEnvelope<IEvent>> fresh) _partitionByCooldown(
      IReadOnlyList<MessageEnvelope<IEvent>> filteredEvents,
      ILookup<Guid, StreamEventData> rawByEventId,
      RecentlyProcessedEventCache? cache,
      string? perspectiveName = null) {
    var cooled = new List<MessageEnvelope<IEvent>>();
    var fresh = new List<MessageEnvelope<IEvent>>();
    if (cache is null) {
      fresh.AddRange(filteredEvents);
      return (cooled, fresh);
    }
    foreach (var envelope in filteredEvents) {
      var rawRows = rawByEventId[envelope.MessageId.Value];
      var rawSeen = false;
      var allCooled = true;
      foreach (var raw in rawRows) {
        if (perspectiveName is not null
            && raw.PerspectiveName is not null
            && !string.Equals(raw.PerspectiveName, perspectiveName, StringComparison.Ordinal)) {
          continue;
        }
        rawSeen = true;
        if (!cache.WasRecentlyProcessed(raw.EventWorkId)) {
          allCooled = false;
          break;
        }
      }
      if (rawSeen && allCooled) {
        cooled.Add(envelope);
      } else {
        fresh.Add(envelope);
      }
    }
    return (cooled, fresh);
  }

  internal static bool _shouldSkipApplyDueToCooldown(
      IReadOnlyList<MessageEnvelope<IEvent>> filteredEvents,
      ILookup<Guid, StreamEventData> rawByEventId,
      RecentlyProcessedEventCache? cache,
      string? perspectiveName = null) {
    if (cache is null || filteredEvents.Count == 0) {
      return false;
    }
    foreach (var envelope in filteredEvents) {
      var rawRows = rawByEventId[envelope.MessageId.Value];
      var rawSeen = false;
      foreach (var raw in rawRows) {
        // Per-perspective filter: when perspectiveName is supplied, only consider raw rows
        // whose PerspectiveName matches. Rows for OTHER perspectives' work_ids are irrelevant
        // to the current perspective's cooldown decision. See JDX 2026-05-04 multi-perspective
        // fanout silent-skip bug.
        if (perspectiveName is not null
            && raw.PerspectiveName is not null
            && !string.Equals(raw.PerspectiveName, perspectiveName, StringComparison.Ordinal)) {
          continue;
        }
        rawSeen = true;
        if (!cache.WasRecentlyProcessed(raw.EventWorkId)) {
          return false;
        }
      }
      // ILookup returns an empty enumerable for missing keys. If we never saw any raw row
      // for this envelope (after filtering), we cannot prove every event_work_id is cooled —
      // default to running the apply. Otherwise a mapping mismatch silently strands the event
      // and prevents PostAllPerspectives from firing (saga events on JDNext, repro'd by
      // CooldownGateDecisionTests.ShouldSkip_EnvelopeMessageIdNotInRawLookup_ReturnsFalse).
      if (!rawSeen) {
        return false;
      }
    }
    return true;
  }

  /// <summary>
  /// Marks every event_work_id for these envelopes (filtered by <paramref name="perspectiveName"/>
  /// when populated) as recently processed. Called after a successful apply or rewind so
  /// subsequent drain ticks within the TTL window short-circuit.
  /// </summary>
  /// <remarks>
  /// JDX 2026-05-04 silent-skip bug: marking ALL raw rows under an EventId — not just the
  /// current perspective's — would put OTHER perspectives' work_ids into the cooldown cache.
  /// On the next drain those perspectives would short-circuit before Apply, never updating
  /// their model. The per-perspective filter scopes the mark to the current perspective only.
  /// </remarks>
  internal static void _markProcessedInCooldown(
      RecentlyProcessedEventCache? cache,
      List<MessageEnvelope<IEvent>> filteredEvents,
      ILookup<Guid, StreamEventData> rawByEventId,
      string? perspectiveName = null) {
    if (cache is null || filteredEvents.Count == 0) {
      return;
    }
    foreach (var envelope in filteredEvents) {
      foreach (var raw in rawByEventId[envelope.MessageId.Value]) {
        if (perspectiveName is not null
            && raw.PerspectiveName is not null
            && !string.Equals(raw.PerspectiveName, perspectiveName, StringComparison.Ordinal)) {
          continue;
        }
        cache.MarkProcessed(raw.EventWorkId);
      }
    }
  }

  /// <summary>
  /// Runs immediately after a successful RunWithEventsAsync and before the coordinator's
  /// ReportCompletionAsync call. Covers cursor update, batch-tracker bookkeeping, the
  /// PostPerspectiveInline + ImmediateDetached lifecycle receptor fans, and the WhenAll
  /// SignalPerspectiveComplete callouts.
  /// </summary>
  private async Task _applyDrainModePerspectiveCompletionAsync(
      PerspectiveStreamContext streamCtx,
      List<MessageEnvelope<IEvent>> filteredEvents,
      PerspectiveCursorCompletion result,
      DrainBatchContext batchContext,
      CancellationToken ct) {
    // Slice 26.17: mark cooldown FIRST, BEFORE cursor cache update. Invariant: if cursor
    // cache advances, cooldown must already contain the work_ids of the events applied to
    // produce that advance. Reversing this order (cursor first, cooldown later) was the
    // dominant residual inversion cause in JDX run 16: when a lifecycle receptor invoker
    // between cursor update and the deferred cooldown-mark call threw or the lease
    // cancelled, cursor advanced but cooldown stayed empty. The next drain saw pending
    // events that weren't in cooldown, the inversion detector compared them against the
    // advanced cursor, and triggered a spurious full-replay rewind. With the order
    // flipped, the failure mode is safe: cooldown set + cursor not yet advanced means the
    // next drain finds all events cooled and short-circuits via _signalCooldownSkippedEvents.
    _markProcessedInCooldown(_recentlyProcessedEventCache, filteredEvents, batchContext.RawByEventId, streamCtx.PerspectiveName);

    _cursorCache.Set(streamCtx.StreamId, streamCtx.PerspectiveName, result.LastEventId);

    // Slice 26.11: track commit_sequence cursor alongside event_id. Find the latest
    // commit_sequence among events that share the result's LastEventId (the runner applies
    // events in order and reports the last). Falls back to keeping the prior cursor when
    // the lookup is null (e.g., result.LastEventId not in rawByEventId, or no stamped row).
    if (result.LastEventId != Guid.Empty) {
      var lastSeq = batchContext.RawByEventId[result.LastEventId]
        .Where(raw => raw.CommitSequence.HasValue)
        .Select(raw => (long?)raw.CommitSequence!.Value)
        .DefaultIfEmpty(null)
        .Max();
      if (lastSeq.HasValue) {
        _cursorCache.SetCommitSequence(streamCtx.StreamId, streamCtx.PerspectiveName, lastSeq);
      }
    }

    foreach (var envelope in filteredEvents) {
      var id = envelope.MessageId.Value;
      batchContext.BatchProcessedEvents.TryAdd(id, (envelope, streamCtx.StreamId));
      // Drain mode has no rewind: every event reaching here is new.
      batchContext.BatchIsNewByEventId.AddOrUpdate(id, true, (_, _) => true);
    }

    // Fire PostPerspectiveInline + ImmediateDetached. The invoker is a no-op when
    // no receptors exist at the stage (compile-time or runtime), so skipping based
    // on a startup-cached flag would miss integration-test receptors registered later.
    await _invokeLifecycleReceptorsForEventsAsync(
      filteredEvents, streamCtx, result.PerspectiveType, result.LastEventId,
      LifecycleStage.PostPerspectiveInline, LifecycleReplayOptions.None, ct);
    await _invokeLifecycleReceptorsForEventsAsync(
      filteredEvents, streamCtx, result.PerspectiveType, result.LastEventId,
      LifecycleStage.ImmediateDetached, LifecycleReplayOptions.None, ct);

    if (batchContext.LifecycleCoordinator is not null) {
      foreach (var envelope in filteredEvents) {
        batchContext.LifecycleCoordinator.SignalPerspectiveComplete(envelope.MessageId.Value, streamCtx.PerspectiveName);
      }
    }
  }

  /// <summary>
  /// Enqueues one PerspectiveEventCompletion per (EventId, EventWorkId) raw row, then fires
  /// OnPerspectiveEventProcessed when the stream produced any events. Splitting this off keeps
  /// the ordering of the original monolith's two "if Completed" blocks around ReportCompletion.
  /// </summary>
  private void _enqueueDrainModePerspectiveCompletions(
      Guid streamId,
      string perspectiveName,
      List<MessageEnvelope<IEvent>> filteredEvents,
      ILookup<Guid, StreamEventData> rawByEventId) {
    foreach (var envelope in filteredEvents) {
      foreach (var rawEvent in rawByEventId[envelope.MessageId.Value]) {
        _pendingEventCompletions.Enqueue(new PerspectiveEventCompletion {
          EventWorkId = rawEvent.EventWorkId
        });
      }
    }

    if (filteredEvents.Count > 0) {
      OnPerspectiveEventProcessed?.Invoke(new PerspectiveEventProcessedEvent {
        PerspectiveName = perspectiveName,
        StreamId = streamId,
        EventCount = filteredEvents.Count
      });
    }
  }


  /// <summary>
  /// <para>Reconciles completion state and groups work items for processing.</para>
  ///
  /// <para>Historically extracted acknowledgement counts from SQL-returned metadata
  /// (perspective_completions_processed / _failures_processed). That path is
  /// silently dropped when the SQL response has no perspective/outbox/inbox
  /// rows — post-burst idle cycles leave completions stuck in "Sent" state
  /// until ResetStale resets them (5–60 min backoff), producing repeating
  /// "Perspective batch: completed=N" log entries that resend the same
  /// completions indefinitely. Matches the bug fixed in
  /// WorkCoordinatorPublisherWorker.</para>
  ///
  /// <para>Fix: acknowledge the local sent-count captured at submission time. The
  /// SQL's perspective_completions_processed is just
  /// jsonb_array_length(p_perspective_completions), so it always equals
  /// what we sent — the round-trip is redundant.</para>
  /// </summary>
  private List<IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork>>
    _reconcileAcknowledgementsAndPrepareWork(
      WorkBatch workBatch,
      int sentCompletionCount,
      int sentFailureCount) {

    // 5-6. Acknowledge the local sent-count (not the SQL-returned metadata).
    _completionStrategy.MarkAsAcknowledged(sentCompletionCount, sentFailureCount);

    // 6a. DB confirmed completion — start TTL countdown for in-flight dedup entries
    _processedEventCache.ActivateRetention();

    // 7. Clear only Acknowledged items
    _completionStrategy.ClearAcknowledged();

    // 8. Reset stale items (sent but not acknowledged for > timeout) back to Pending
    _completionStrategy.ResetStale(DateTimeOffset.UtcNow);

    // 9. Dedup: filter out work items already processed within retention window
    var dedupedWork = _filterDuplicateWorkItems(workBatch.PerspectiveWork);

    // Group perspective work items by (StreamId, PerspectiveName)
    // Each work item represents a single event, but the runner processes ALL events for a stream
    // So we only call RunAsync() ONCE per (stream, perspective) pair
    var groupedWork = dedupedWork
      .GroupBy(w => (StreamId: w.StreamId, PerspectiveName: w.PerspectiveName))
      .ToList();

    return groupedWork;
  }

  /// <summary>
  /// Records batch composition metrics and sets tracing tags on the batch activity.
  /// </summary>
  private void _recordBatchMetrics(
      Activity? batchActivity,
      WorkBatch workBatch,
      List<IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork>> groupedWork,
      PerspectiveCursorCompletion[] completionsToSend,
      PerspectiveCursorFailure[] failuresToSend) {

    // perspectivesPerEventType is built once at startup from the registry.
    // It maps event_type → ALL perspective names, ensuring WhenAll expectations
    // are complete regardless of which perspectives are in this batch.

    // Record batch composition metrics
    _metrics?.BatchWorkItems.Record(workBatch.PerspectiveWork.Count);
    _metrics?.BatchStreamGroups.Record(groupedWork.Count);

    // Add batch metrics to parent span for tracing visibility
    batchActivity?.SetTag("whizbang.perspective.batch.work_items", workBatch.PerspectiveWork.Count);
    batchActivity?.SetTag("whizbang.perspective.batch.groups", groupedWork.Count);
    batchActivity?.SetTag("whizbang.perspective.batch.completions_sent", completionsToSend.Length);
    batchActivity?.SetTag("whizbang.perspective.batch.failures_sent", failuresToSend.Length);
  }

  /// <summary>
  /// Logs diagnostic information about the batch composition.
  /// </summary>
  private void _logBatchComposition(
      WorkBatch workBatch,
      List<IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork>> groupedWork) {

#pragma warning disable CA1848, CA1873 // Diagnostic logging for perspective work batch
    _logger.LogDebug("ProcessWorkBatchAsync returned: PerspectiveWork count: {WorkCount}, Grouped into {GroupCount} unique (StreamId, PerspectiveName) pairs",
      workBatch.PerspectiveWork.Count, groupedWork.Count);
    foreach (var g in groupedWork) {
      _logger.LogDebug("  - {PerspectiveName}/{StreamId}: {ItemCount} work items", g.Key.PerspectiveName, g.Key.StreamId, g.Count());
    }
    if (workBatch.PerspectiveWork.Count == 0) {
      _logger.LogDebug("NO PERSPECTIVE WORK CLAIMED - check wh_message_associations and wh_perspective_cursors");
    }
#pragma warning restore CA1848, CA1873
  }

  /// <summary>
  /// Phase 1: Resolves runner, event store, loads upcoming events, and extracts trace context
  /// for a single perspective group. Returns null runner if resolution fails (caller should skip).
  /// </summary>
  private async Task<(PerspectiveCursorInfo? Checkpoint, IPerspectiveRunner? Runner, IEventStore? EventStore,
                       List<MessageEnvelope<IEvent>>? UpcomingEvents, ActivityContext PerspectiveParentContext)>
    _resolveDependenciesAndLoadEventsAsync(
      AsyncServiceScope scope,
      IWorkCoordinator workCoordinator,
      IReceptorInvoker? receptorInvoker,
      Guid streamId,
      string perspectiveName,
      Activity? batchActivity,
      ActivityContext effectiveParent,
      CancellationToken cancellationToken) {

    // Look up the checkpoint to get the LastProcessedEventId
    var checkpointSw = System.Diagnostics.Stopwatch.StartNew();
    var checkpoint = await workCoordinator.GetPerspectiveCursorAsync(
      streamId, perspectiveName, cancellationToken);
    _metrics?.CheckpointDuration.Record(checkpointSw.Elapsed.TotalMilliseconds);

    var lastProcessedEventId = checkpoint?.LastEventId;

    if (_logger.IsEnabled(LogLevel.Information)) {
      var lastProcessedStr = lastProcessedEventId?.ToString() ?? "null (never processed)";
      LogProcessingPerspectiveCursor(_logger, perspectiveName, streamId, lastProcessedStr);
    }

    // Resolve the generated IPerspectiveRunner for this perspective
    var registry = scope.ServiceProvider.GetService<IPerspectiveRunnerRegistry>();
    if (registry == null) {
      LogPerspectiveRunnerRegistryNotRegistered(_logger, perspectiveName);
      return (checkpoint, null, null, null, default);
    }

    // DIAGNOSTIC: Log registry resolution details
    LogRunnerRegistryResolved(_logger, perspectiveName, registry.GetType().FullName ?? "unknown", registry.GetHashCode());

    var runner = registry.GetRunner(perspectiveName, scope.ServiceProvider);
    if (runner == null) {
      LogNoPerspectiveRunnerFound(_logger, perspectiveName, streamId);
      return (checkpoint, null, null, null, default);
    }

    // DIAGNOSTIC: Log runner resolution details
    LogRunnerInstanceResolved(_logger, perspectiveName, runner.GetType().FullName ?? "unknown", runner.GetHashCode());

    // Resolve IEventStore from scope (it's registered as scoped, not singleton)
    var eventStore = scope.ServiceProvider.GetService<IEventStore>();

    // DIAGNOSTIC: Log lifecycle invocation dependencies for debugging
    LogLifecycleDependenciesResolved(_logger,
      perspectiveName, streamId,
      receptorInvoker is not null, eventStore is not null, _eventTypeProvider is not null);

    // Load events early to extract trace context for distributed tracing
    var (upcomingEvents, perspectiveParentContext) = await _loadUpcomingEventsAndExtractTraceContextAsync(
      eventStore, streamId, lastProcessedEventId, batchActivity, effectiveParent, cancellationToken);

    return (checkpoint, runner, eventStore, upcomingEvents, perspectiveParentContext);
  }

  /// <summary>
  /// Loads upcoming events from the event store and extracts trace context from the first event's hops.
  /// This links perspective spans to the original request that created the events.
  /// </summary>
  private async Task<(List<MessageEnvelope<IEvent>>? UpcomingEvents, ActivityContext ParentContext)>
    _loadUpcomingEventsAndExtractTraceContextAsync(
      IEventStore? eventStore,
      Guid streamId,
      Guid? lastProcessedEventId,
      Activity? batchActivity,
      ActivityContext effectiveParent,
      CancellationToken cancellationToken) {

    List<MessageEnvelope<IEvent>>? upcomingEvents = null;
    var perspectiveParentContext = batchActivity is null ? effectiveParent : default;

    if (eventStore is not null && _eventTypeProvider is not null) {
      var eventTypes = _eventTypeProvider.GetEventTypes();
      if (eventTypes.Count > 0) {
        var eventLoadSw = System.Diagnostics.Stopwatch.StartNew();
        upcomingEvents = await eventStore.GetEventsBetweenPolymorphicAsync(
          streamId,
          lastProcessedEventId,
          Guid.Empty, // Read all events after lastProcessedEventId
          eventTypes,
          cancellationToken
        );
        _metrics?.EventLoadDuration.Record(eventLoadSw.Elapsed.TotalMilliseconds);
        _metrics?.BatchEventCount.Record(upcomingEvents.Count);

        // Extract trace context from the first event's hops
        if (upcomingEvents.Count > 0) {
          var firstEvent = upcomingEvents[0];
          var traceParent = firstEvent.Hops
            .Where(h => h.Type == HopType.Current)
            .Select(h => h.TraceParent)
            .LastOrDefault(tp => tp is not null);

          if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out var extractedContext)) {
            perspectiveParentContext = extractedContext;
          }
        }
      }
    }

    return (upcomingEvents, perspectiveParentContext);
  }

  /// <summary>
  /// Sets diagnostic tags on the perspective activity span.
  /// </summary>
  private static void _tagPerspectiveActivity(
      Activity? perspectiveActivity,
      string perspectiveName,
      Guid streamId,
      List<MessageEnvelope<IEvent>>? upcomingEvents,
      ActivityContext perspectiveParentContext) {

    perspectiveActivity?.SetTag("whizbang.perspective.name", perspectiveName);
    perspectiveActivity?.SetTag("whizbang.stream.id", streamId.ToString());

    // DIAGNOSTIC: Help debug orphaned perspective spans
    perspectiveActivity?.SetTag("whizbang.perspective.events_loaded", upcomingEvents?.Count ?? 0);
    perspectiveActivity?.SetTag("whizbang.perspective.has_parent_context", perspectiveParentContext != default);
    if (upcomingEvents is { Count: > 0 }) {
      var firstEventTraceParent = upcomingEvents[0].Hops
        .Where(h => h.Type == HopType.Current)
        .Select(h => h.TraceParent)
        .LastOrDefault();
      perspectiveActivity?.SetTag("whizbang.perspective.first_event_traceparent", firstEventTraceParent ?? "(none)");
    }
  }

  /// <summary>
  /// Phase 3.1: Invokes PrePerspective lifecycle stages via coordinator (exactly-once per event)
  /// or falls back to direct invocation when coordinator is not registered.
  /// </summary>
  private async Task _invokePrePerspectiveLifecycleAsync(
      List<MessageEnvelope<IEvent>>? upcomingEvents,
      bool enableLifecycleSpans,
      ILifecycleCoordinator? lifecycleCoordinator,
      IReceptorInvoker? receptorInvoker,
      PerspectiveStreamContext streamCtx,
      IPerspectiveRunner runner,
      CancellationToken cancellationToken) {

    using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PrePerspective", ActivityKind.Internal) : null) {
      if (upcomingEvents is { Count: > 0 }) {
        try {
          foreach (var envelope in upcomingEvents) {
            await _establishSecurityContextAsync(envelope, streamCtx.ScopedProvider, cancellationToken);

            if (lifecycleCoordinator is not null) {
              // Coordinator path: BeginTracking + AdvanceToAsync (stage guard = exactly-once)
              var tracking = lifecycleCoordinator.BeginTracking(
                envelope.MessageId.Value, envelope, LifecycleStage.PrePerspectiveDetached,
                MessageSource.Local, streamCtx.StreamId, runner.PerspectiveType);

              // Stage guard ensures these fire once per event, not once per perspective group
              await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveDetached, streamCtx.ScopedProvider, cancellationToken);
              await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveInline, streamCtx.ScopedProvider, cancellationToken);
            } else if (receptorInvoker is not null) {
              // Fallback: direct invocation when coordinator not registered
              var context = new LifecycleExecutionContext {
                CurrentStage = LifecycleStage.PrePerspectiveDetached,
                StreamId = streamCtx.StreamId,
                LastProcessedEventId = streamCtx.LastProcessedEventId,
                MessageSource = MessageSource.Local,
                AttemptNumber = 1
              };
              // Detached: fire-and-forget with own DI scope
              _fireDetachedStageAsync(envelope, LifecycleStage.PrePerspectiveDetached, context, cancellationToken);
              // Inline: blocks pipeline
              await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PrePerspectiveInline,
                context with { CurrentStage = LifecycleStage.PrePerspectiveInline }, cancellationToken);
              await receptorInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateDetached,
                context with { CurrentStage = LifecycleStage.ImmediateDetached }, cancellationToken);
            }
          }
        } catch (Exception ex) {
          LogErrorInvokingLifecycleReceptors(_logger, ex, streamCtx.PerspectiveName, streamCtx.StreamId);
          throw;
        }
      }
    }
  }

  /// <summary>
  /// Executes the perspective runner via the rewind path or normal path,
  /// including snapshot bootstrap when needed.
  /// Returns the result and the processing mode used.
  /// When the rewind path cannot acquire a lock, RewindLockSkipped=true is returned.
  /// </summary>
  private async Task<(PerspectiveCursorCompletion Result, ProcessingMode? Mode, bool RewindLockSkipped)>
    _executePerspectiveRunnerAsync(
      IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork> group,
      IPerspectiveRunner runner,
      PerspectiveCursorInfo? checkpoint,
      PerspectiveStreamContext streamCtx,
      bool enablePerspectiveSpans,
      CancellationToken cancellationToken) {

    // Check if cursor has RewindRequired flag (set by Phase 4.6B when out-of-order events detected)
    var cursorStatus = checkpoint?.Status ?? PerspectiveProcessingStatus.None;
    var needsRewind = cursorStatus.HasFlag(PerspectiveProcessingStatus.RewindRequired);
    var rewindTriggerEventId = checkpoint?.RewindTriggerEventId;

    if (needsRewind && rewindTriggerEventId.HasValue) {
      var eventsBehind = group.Count();
      LogRewindRequired(_logger, streamCtx.PerspectiveName, streamCtx.StreamId,
        checkpoint?.LastEventId ?? Guid.Empty, rewindTriggerEventId.Value, eventsBehind);
      _metrics?.RewindEventsBehind.Record(eventsBehind,
        new KeyValuePair<string, object?>(METRIC_TAG_PERSPECTIVE_NAME, streamCtx.PerspectiveName));

      var (result, lockSkipped) = await _executeRewindPathAsync(
        runner, streamCtx.StreamId, streamCtx.PerspectiveName, rewindTriggerEventId.Value,
        eventsBehind, enablePerspectiveSpans, cancellationToken);
      return (result, ProcessingMode.Replay, lockSkipped);
    }

    // Bootstrap snapshot if needed (existing stream with events but no snapshots)
    await _bootstrapSnapshotIfNeededAsync(runner, streamCtx.StreamId, streamCtx.PerspectiveName, streamCtx.LastProcessedEventId, cancellationToken);

    // Normal path
    var normalResult = await _executeNormalPathAsync(
      runner, streamCtx.StreamId, streamCtx.PerspectiveName, streamCtx.LastProcessedEventId,
      enablePerspectiveSpans, cancellationToken);
    return (normalResult, null, false);
  }

  /// <summary>
  /// Rewind path: acquire stream lock, restore from snapshot and replay events.
  /// Throws OperationCanceledException if lock cannot be acquired (caller handles via continue).
  /// </summary>
  private async Task<(PerspectiveCursorCompletion Result, bool LockSkipped)> _executeRewindPathAsync(
      IPerspectiveRunner runner,
      Guid streamId,
      string perspectiveName,
      Guid rewindTriggerEventId,
      int eventsBehind,
      bool enablePerspectiveSpans,
      CancellationToken cancellationToken) {

    PerspectiveCursorCompletion result;
    var lockAcquired = false;
    try {
      if (_streamLocker is not null) {
        lockAcquired = await _streamLocker.TryAcquireLockAsync(
          streamId, perspectiveName, _instanceProvider.InstanceId, "rewind", cancellationToken);
        if (!lockAcquired) {
          LogFailedToAcquireRewindLock(_logger, perspectiveName, streamId);
          return (new PerspectiveCursorCompletion {
            StreamId = streamId,
            PerspectiveName = perspectiveName,
            LastEventId = Guid.Empty,
            Status = PerspectiveProcessingStatus.None
          }, LockSkipped: true);
        }
      }

      // Start keepalive if lock was acquired
      using var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      var keepaliveTask = lockAcquired
        ? _startLockKeepaliveAsync(streamId, perspectiveName, keepaliveCts.Token)
        : Task.CompletedTask;

      using (var activity = enablePerspectiveSpans ? WhizbangActivitySource.Tracing.StartActivity("Perspective RewindAndRunAsync", ActivityKind.Internal) : null) {
        activity?.SetTag("whizbang.perspective.name", perspectiveName);
        activity?.SetTag("whizbang.stream.id", streamId.ToString());
        activity?.SetTag("whizbang.perspective.rewind_trigger_event_id", rewindTriggerEventId.ToString());

        var runnerSw = System.Diagnostics.Stopwatch.StartNew();
        try {
          result = await runner.RewindAndRunAsync(
            streamId, perspectiveName, rewindTriggerEventId, cancellationToken);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          // Isolate rewind failures — a single stream's failure must not crash the worker.
          // The stream will retry on the next polling cycle.
          LogRewindFailed(_logger, ex, perspectiveName, streamId, rewindTriggerEventId);
          _metrics?.Errors.Add(1);
          activity?.SetTag("whizbang.perspective.rewind.error", ex.Message);
          activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

          return (new PerspectiveCursorCompletion {
            StreamId = streamId,
            PerspectiveName = perspectiveName,
            LastEventId = Guid.Empty,
            Status = PerspectiveProcessingStatus.None
          }, LockSkipped: false);
        }

        var rewindDurationMs = runnerSw.Elapsed.TotalMilliseconds;
        _metrics?.RunnerDuration.Record(rewindDurationMs);

        // Rewind-specific meters
        var hasSnapshot = _snapshotStore is not null;
        _metrics?.Rewinds.Add(1,
          new KeyValuePair<string, object?>(METRIC_TAG_PERSPECTIVE_NAME, perspectiveName),
          new KeyValuePair<string, object?>("has_snapshot", hasSnapshot));
        _metrics?.RewindDuration.Record(rewindDurationMs,
          new KeyValuePair<string, object?>(METRIC_TAG_PERSPECTIVE_NAME, perspectiveName));
        _metrics?.RewindEventsReplayed.Record(result.EventsProcessed,
          new KeyValuePair<string, object?>(METRIC_TAG_PERSPECTIVE_NAME, perspectiveName));

        // Span enrichment
        activity?.SetTag("whizbang.perspective.status", result.Status.ToString());
        activity?.SetTag("whizbang.perspective.last_event_id", result.LastEventId.ToString());
        activity?.SetTag("whizbang.perspective.rewind.events_behind", eventsBehind);
        activity?.SetTag("whizbang.perspective.rewind.events_replayed", result.EventsProcessed);
        activity?.SetTag("whizbang.perspective.rewind.has_snapshot", hasSnapshot);
        activity?.SetTag("whizbang.perspective.rewind.replay_source", hasSnapshot ? "snapshot" : "full");

        // Completion log — hasSnapshot indicates store availability, not actual usage.
        // The runner logs the actual snapshot decision (restore from snapshot vs full replay).
        LogRewindCompleted(_logger, perspectiveName, streamId, result.EventsProcessed,
          (long)rewindDurationMs, hasSnapshot ? "snapshot store available" : "no snapshot store");
      }

      // Stop keepalive
      await keepaliveCts.CancelAsync();
      try { await keepaliveTask; } catch (OperationCanceledException) { /* expected */ }
    } finally {
      if (lockAcquired && _streamLocker is not null) {
        await _streamLocker.ReleaseLockAsync(streamId, perspectiveName, _instanceProvider.InstanceId, cancellationToken);
      }
    }

    return (result, LockSkipped: false);
  }

  /// <summary>
  /// Bootstrap snapshot for an existing stream that has events but no snapshots yet.
  /// Skips if already bootstrapped this session.
  /// </summary>
  private async Task _bootstrapSnapshotIfNeededAsync(
      IPerspectiveRunner runner,
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      CancellationToken cancellationToken) {

    if (_snapshotStore is null || !lastProcessedEventId.HasValue
        || _bootstrappedThisSession.ContainsKey((streamId, perspectiveName))) {
      return;
    }

    var lockAcquired = false;
    try {
      var hasSnapshots = await _snapshotStore.HasAnySnapshotAsync(streamId, perspectiveName, cancellationToken);
      if (!hasSnapshots) {
        if (_streamLocker is not null) {
          lockAcquired = await _streamLocker.TryAcquireLockAsync(
            streamId, perspectiveName, _instanceProvider.InstanceId, "bootstrap", cancellationToken);
        }
        // Bootstrap even without lock (graceful degradation)
        await runner.BootstrapSnapshotAsync(streamId, perspectiveName, lastProcessedEventId.Value, cancellationToken);
      }
      _bootstrappedThisSession.TryAdd((streamId, perspectiveName), 0);
    } finally {
      if (lockAcquired && _streamLocker is not null) {
        await _streamLocker.ReleaseLockAsync(streamId, perspectiveName, _instanceProvider.InstanceId, cancellationToken);
      }
    }
  }

  /// <summary>
  /// Normal path: run the perspective runner for the given stream/perspective.
  /// </summary>
  private async Task<PerspectiveCursorCompletion> _executeNormalPathAsync(
      IPerspectiveRunner runner,
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      bool enablePerspectiveSpans,
      CancellationToken cancellationToken) {

    using var activity = enablePerspectiveSpans
      ? WhizbangActivitySource.Tracing.StartActivity("Perspective RunAsync", ActivityKind.Internal)
      : null;
    activity?.SetTag("whizbang.perspective.name", perspectiveName);
    activity?.SetTag("whizbang.stream.id", streamId.ToString());
    activity?.SetTag("whizbang.perspective.last_processed_event_id", lastProcessedEventId?.ToString() ?? "null");

    var runnerSw = System.Diagnostics.Stopwatch.StartNew();
    var result = await runner.RunAsync(
      streamId, perspectiveName, lastProcessedEventId, cancellationToken);
    _metrics?.RunnerDuration.Record(runnerSw.Elapsed.TotalMilliseconds);

    activity?.SetTag("whizbang.perspective.status", result.Status.ToString());
    activity?.SetTag("whizbang.perspective.last_event_id", result.LastEventId.ToString());

    return result;
  }

  /// <summary>
  /// Phase 3a: Loads processed events with diagnostic logging.
  /// Only loads when receptor invoker and event store are available and processing completed.
  /// </summary>
  private async Task<List<MessageEnvelope<IEvent>>> _loadAndLogProcessedEventsAsync(
      IReceptorInvoker? receptorInvoker,
      IEventStore? eventStore,
      PerspectiveCursorCompletion result,
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      CancellationToken cancellationToken) {

    var shouldLoadEvents = receptorInvoker is not null && eventStore is not null && result.Status == PerspectiveProcessingStatus.Completed;
    if (_logger.IsEnabled(LogLevel.Debug)) {
      var hasInvoker = receptorInvoker is not null;
      var hasStore = eventStore is not null;
      var statusStr = result.Status.ToString();
      var lastProcessed = lastProcessedEventId.GetValueOrDefault();
      var current = result.LastEventId;
      LogDiagnosticLoadingEvents(_logger, perspectiveName, streamId, shouldLoadEvents, hasInvoker, hasStore, statusStr, lastProcessed, current);
    }

    var processedEvents = shouldLoadEvents
      ? await _loadProcessedEventsAsync(eventStore!, streamId, perspectiveName, lastProcessedEventId, result.LastEventId, cancellationToken)
      : [];

    if (_logger.IsEnabled(LogLevel.Debug)) {
      var eventsCount = processedEvents.Count;
      LogDiagnosticLoadedEvents(_logger, eventsCount, perspectiveName, streamId);
    }

    return processedEvents;
  }

  /// <summary>
  /// Phase 3c: Reports completion via strategy and signals sync trackers.
  /// </summary>
  private async Task _reportCompletionAndSignalSyncAsync(
      PerspectiveCursorCompletion result,
      List<MessageEnvelope<IEvent>> processedEvents,
      IWorkCoordinator workCoordinator,
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken) {

    // NOTE: PostPerspectiveDetached is fired from the generated perspective runner, not here.
    // The runner fires it after flushing data but before returning the completion.
    // This ensures it fires before checkpoint commits, as designed.

    // Phase 3c: Report completion via strategy (saves checkpoint to database)
    LogReportingCompletion(_logger, perspectiveName, streamId, result.LastEventId);
    await _completionStrategy.ReportCompletionAsync(result, workCoordinator, cancellationToken);
    LogCompletionReported(_logger);

    // Phase 3c.0: Mark processed events in singleton tracker for cross-scope sync
    // This signals any WaitForPerspectiveEventsAsync callers that this perspective has processed these events
    // Note: Uses MarkProcessedByPerspective to only remove THIS perspective's entry, not all perspectives
    if (processedEvents.Count > 0 && _syncEventTracker is not null) {
      var processedEventIds = processedEvents.Select(e => e.MessageId.Value).ToList();
#pragma warning disable CA1848
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("[SYNC_DEBUG] PerspectiveWorker MarkProcessedByPerspective: Perspective={Perspective}, StreamId={StreamId}, EventCount={Count}, EventIds=[{Ids}]",
          perspectiveName, streamId, processedEventIds.Count, string.Join(", ", processedEventIds));
      }
#pragma warning restore CA1848
      _syncEventTracker.MarkProcessedByPerspective(processedEventIds, perspectiveName);
    } else if (_logger.IsEnabled(LogLevel.Debug)) {
#pragma warning disable CA1848
      _logger.LogDebug("[SYNC_DEBUG] PerspectiveWorker MarkProcessed SKIPPED: ProcessedCount={Count}, HasTracker={HasTracker}",
        processedEvents.Count, _syncEventTracker is not null);
#pragma warning restore CA1848
    }

    // Phase 3c.1: Signal checkpoint updated for perspective sync
    // This notifies any waiting sync awaiters that the perspective has processed up to this event
    if (result.PerspectiveType is not null) {
      _syncSignaler?.SignalCheckpointUpdated(result.PerspectiveType, streamId, result.LastEventId);
    }
  }

  /// <summary>
  /// Groups the flag + optional per-batch metadata that the PostPerspective lifecycle invocation
  /// needs alongside the main processed-events/receptor-invoker pair.
  /// </summary>
  private readonly record struct PostPerspectiveLifecycleOptions(
    bool EnableLifecycleSpans,
    ProcessingMode? ProcessingMode,
    IReadOnlyDictionary<Guid, bool>? IsNewByEventId);

  /// <summary>
  /// Phase 3d: Invokes PostPerspective lifecycle receptors and processes tags.
  /// PostPerspective fires PER PERSPECTIVE via direct invoker (not coordinator).
  /// </summary>
  private async Task _invokePostPerspectiveLifecycleAsync(
      List<MessageEnvelope<IEvent>> processedEvents,
      IReceptorInvoker? receptorInvoker,
      PerspectiveStreamContext streamCtx,
      PerspectiveCursorCompletion result,
      PostPerspectiveLifecycleOptions options,
      CancellationToken cancellationToken) {

    LogCheckingPostPerspectiveInline(_logger, processedEvents.Count, receptorInvoker is not null);

    using (options.EnableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostPerspective", ActivityKind.Internal) : null) {
      if (processedEvents.Count > 0 && receptorInvoker is not null) {
        LogInvokingPostPerspectiveInline(_logger, processedEvents.Count, streamCtx.PerspectiveName, streamCtx.StreamId);

        var replayOpts = new LifecycleReplayOptions(options.ProcessingMode, options.IsNewByEventId);
        await _invokeLifecycleReceptorsForEventsAsync(
          processedEvents, streamCtx, result.PerspectiveType, result.LastEventId,
          LifecycleStage.PostPerspectiveInline, replayOpts, cancellationToken);
        await _invokeLifecycleReceptorsForEventsAsync(
          processedEvents, streamCtx, result.PerspectiveType, result.LastEventId,
          LifecycleStage.ImmediateDetached, replayOpts, cancellationToken);
        LogPostPerspectiveInlineCompleted(_logger);

        // Process tags at PostPerspectiveInline (per-perspective, with scope context)
        var tagProcessor = streamCtx.ScopedProvider.GetService<IMessageTagProcessor>();
        if (tagProcessor is not null) {
          foreach (var envelope in processedEvents) {
            var eventPayload = envelope.Payload;
            var eventType = eventPayload.GetType();
            var extractedScope = EnvelopeContextExtractor.ExtractScope(envelope.Hops);
            await tagProcessor.ProcessTagsAsync(
              eventPayload, eventType, LifecycleStage.PostPerspectiveInline,
              extractedScope, cancellationToken).ConfigureAwait(false);
          }
        }
      } else {
        if (processedEvents.Count == 0) {
          LogSkippingPostPerspectiveInlineNoEvents(_logger);
        }
        if (receptorInvoker is null) {
          LogSkippingPostPerspectiveInlineNoInvoker(_logger);
        }
      }
    }
  }

  /// <summary>
  /// Buffers perspective event completions for next batch, updates dedup cache,
  /// and signals perspective completion for WhenAll tracking.
  /// </summary>
  private void _bufferCompletionsAndUpdateCache(
      IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork> group,
      List<MessageEnvelope<IEvent>> processedEvents,
      ILifecycleCoordinator? lifecycleCoordinator,
      string perspectiveName) {

    // Buffer perspective event completions for next batch (triggers wh_perspective_events deletion)
    var completedWorkIds = new List<Guid>(group.Count());
    // S3267: Multi-statement loop body — LINQ would reduce readability
#pragma warning disable S3267
    foreach (var workItem in group) {
      _pendingEventCompletions.Enqueue(new PerspectiveEventCompletion {
        EventWorkId = workItem.WorkId,
        StatusFlags = (int)PerspectiveProcessingStatus.Completed
      });
      completedWorkIds.Add(workItem.WorkId);
    }
#pragma warning restore S3267

    // Mark processed WorkIds as in-flight in dedup cache (no TTL until DB acks)
    _processedEventCache.AddRange(completedWorkIds);

    // Signal this perspective completed for WhenAll tracking
    // PostLifecycle fires only after ALL perspectives signal complete for each event
    if (lifecycleCoordinator is not null) {
      foreach (var envelope in processedEvents) {
        lifecycleCoordinator.SignalPerspectiveComplete(envelope.MessageId.Value, perspectiveName);
      }
    }
  }

  /// <summary>
  /// Phase 5: Fires PostLifecycle once per unique event after ALL perspectives complete (WhenAll).
  /// The coordinator guarantees exactly-once PostLifecycle via stage guards + perspective WhenAll.
  /// Falls back to direct invocation when coordinator is not registered.
  /// </summary>
  private async Task _firePostLifecycleDetached(
      ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> batchProcessedEvents,
      ILifecycleCoordinator? lifecycleCoordinator,
      IReceptorInvoker? receptorInvoker,
      List<IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork>> groupedWork,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken,
      IReadOnlyDictionary<Guid, bool>? batchIsNewByEventId = null) {

    if (batchProcessedEvents.IsEmpty) {
      return;
    }

    if (lifecycleCoordinator is not null) {
      await _firePostLifecycleWithCoordinatorAsync(
        batchProcessedEvents, lifecycleCoordinator, groupedWork, scopedProvider, cancellationToken);
    } else if (receptorInvoker is not null) {
      await _firePostLifecycleFallbackAsync(
        batchProcessedEvents, receptorInvoker, scopedProvider, cancellationToken, _detachedTasks.Add, batchIsNewByEventId);
    }
  }

  /// <summary>
  /// Fires PostLifecycle via coordinator with WhenAll gate and stage guards.
  /// Registers expected perspective completions, replays signals, and advances lifecycle stages.
  /// </summary>
  private async Task _firePostLifecycleWithCoordinatorAsync(
      ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> batchProcessedEvents,
      ILifecycleCoordinator lifecycleCoordinator,
      List<IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork>> groupedWork,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken) {

    // Register expected perspectives for each event using the FULL registry map.
    // This ensures WhenAll expectations include ALL perspectives that handle the event type,
    // not just the ones claimed in this batch. ExpectPerspectiveCompletions is idempotent (TryAdd).
    if (_perspectivesPerEventType is not null) {
      // S3267: Loop has side effects (logging/state mutation) — LINQ not appropriate
#pragma warning disable S3267
      foreach (var (eventId, (envelope, _)) in batchProcessedEvents) {
        var eventType = envelope.Payload.GetType();
        var eventTypeKey = $"{eventType.FullName}, {eventType.Assembly.GetName().Name}";
        if (_perspectivesPerEventType.TryGetValue(eventTypeKey, out var expected)) {
          lifecycleCoordinator.ExpectPerspectiveCompletions(eventId, expected);
        } else {
          LogEventTypeNotInPerspectiveRegistry(_logger, eventTypeKey);
        }
      }
#pragma warning restore S3267
    }

    // Replay signals — perspectives already completed during the group loop, but
    // expectations may have been registered just above. Replaying ensures WhenAll resolves.
    foreach (var groupKey in groupedWork.Select(g => g.Key)) {
      var gPerspectiveName = groupKey.PerspectiveName;
      foreach (var (eventId, _) in batchProcessedEvents.Where(e => e.Value.StreamId == groupKey.StreamId)) {
        lifecycleCoordinator.SignalPerspectiveComplete(eventId, gPerspectiveName);
      }
    }

    foreach (var (eventId, (envelope, _)) in batchProcessedEvents) {
      // WhenAll gate: PostAllPerspectives fires only when all perspectives signaled complete
      if (!lifecycleCoordinator.AreAllPerspectivesComplete(eventId)) {
        // Not all perspectives have completed yet — keep tracking alive for next batch.
        // Don't abandon: the tracking instance preserves the stage guard so
        // PostAllPerspectivesDetached fires exactly once across all batch cycles.
        continue;
      }

      try {
        await _establishSecurityContextAsync(envelope, scopedProvider, cancellationToken);

        // Get existing tracking (created during PrePerspective via BeginTracking/GetOrAdd)
        var tracking = lifecycleCoordinator.GetTracking(eventId);
        if (tracking is not null) {
          // PostAllPerspectives: fires once per event after ALL perspectives complete (new stage)
          await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesDetached, scopedProvider, cancellationToken);
          await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesInline, scopedProvider, cancellationToken);
          coordinatorMetrics?.PostAllPerspectivesFired.Add(1);

          // PostLifecycle: fires once per event as the final lifecycle stage
          await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, scopedProvider, cancellationToken);
          await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scopedProvider, cancellationToken);
          coordinatorMetrics?.PostLifecycleFired.Add(1);

          // Record durable lifecycle completion marker for crash recovery
          await _recordLifecycleCompletionAsync(eventId, scopedProvider, cancellationToken);
        }
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        // Isolate per-event errors — one failing receptor must not prevent other events
        // from firing PostLifecycle. Without this, a single throwing receptor kills the
        // entire batch loop and all subsequent events never get PostLifecycle.
        LogPostLifecycleError(_logger, ex, eventId);
        coordinatorMetrics?.PostLifecycleErrors.Add(1);
      }

      // DON'T abandon tracking after stages fire — the tracking instance's stage guard
      // prevents PostAllPerspectivesDetached from firing again in subsequent batch cycles.
      // The tracking is marked _isComplete after PostLifecycleInline (see LifecycleTrackingState),
      // so all future AdvanceToAsync calls return immediately.
      // Memory cleanup happens naturally as events age out of batchProcessedEvents.
    }
  }

  /// <summary>
  /// Fallback: direct invocation of PostLifecycle when coordinator is not registered (no WhenAll guarantee).
  /// </summary>
  private static async Task _firePostLifecycleFallbackAsync(
      ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> batchProcessedEvents,
      IReceptorInvoker receptorInvoker,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken,
      Action<Task>? trackDetachedTask = null,
      IReadOnlyDictionary<Guid, bool>? batchIsNewByEventId = null) {

    foreach (var (eventId, (envelope, streamId)) in batchProcessedEvents) {
      var isNew = batchIsNewByEventId is null
        || !batchIsNewByEventId.TryGetValue(eventId, out var flag)
        || flag;
      var context = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PostLifecycleDetached,
        StreamId = streamId,
        PerspectiveType = null,
        MessageSource = MessageSource.Local,
        AttemptNumber = 1,
        // When the batch tracked this event as already-processed, signal Replay mode so
        // the receptor filter suppresses non-idempotent receptors at the WhenAll gate.
        ProcessingMode = isNew ? null : Messaging.ProcessingMode.Replay,
        IsNewEvent = isNew
      };

      await _establishSecurityContextAsync(envelope, scopedProvider, cancellationToken);
      // Detached: fire-and-forget with own DI scope
      var scopeFactory = scopedProvider.GetRequiredService<IServiceScopeFactory>();
      var detachedTask = _fireDetachedStageStaticAsync(scopeFactory, envelope, LifecycleStage.PostLifecycleDetached, context);
      trackDetachedTask?.Invoke(detachedTask);
      // Inline: blocks pipeline
      await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PostLifecycleInline,
        context with { CurrentStage = LifecycleStage.PostLifecycleInline }, cancellationToken);
      await receptorInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateDetached,
        context with { CurrentStage = LifecycleStage.ImmediateDetached }, cancellationToken);
    }
  }

  /// <summary>
  /// Fires a Detached lifecycle stage as fire-and-forget with its own DI scope.
  /// </summary>
  private void _fireDetachedStageAsync(
      MessageEnvelope<IEvent> envelope, LifecycleStage stage,
      LifecycleExecutionContext context, CancellationToken ct) {
    var task = Task.Run(async () => {
      try {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await _establishSecurityContextAsync(envelope, scope.ServiceProvider, ct);
        var invoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
        if (invoker is null) {
          return;
        }
        var ctx = context with { CurrentStage = stage };
        await invoker.InvokeAsync(envelope, stage, ctx, ct);
        await invoker.InvokeAsync(envelope, LifecycleStage.ImmediateDetached,
          ctx with { CurrentStage = LifecycleStage.ImmediateDetached }, ct);
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        // Graceful shutdown
      } catch (Exception ex) {
        LogDetachedStageError(_logger, ex, stage, envelope.MessageId.Value);
      }
    }, ct);
    _detachedTasks.Add(task);
  }

  /// <summary>
  /// Waits for all in-flight detached tasks to complete.
  /// Used for graceful shutdown and testing.
  /// </summary>
  internal async ValueTask DrainDetachedAsync() {
    await Task.WhenAll(_detachedTasks).ConfigureAwait(false);
  }

  private static Task _fireDetachedStageStaticAsync(
      IServiceScopeFactory scopeFactory, MessageEnvelope<IEvent> envelope,
      LifecycleStage stage, LifecycleExecutionContext context) {
    return Task.Run(async () => {
      try {
        await using var scope = scopeFactory.CreateAsyncScope();
        await _establishSecurityContextAsync(envelope, scope.ServiceProvider, default);
        var invoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
        if (invoker is null) {
          return;
        }
        var ctx = context with { CurrentStage = stage };
        await invoker.InvokeAsync(envelope, stage, ctx, default);
        await invoker.InvokeAsync(envelope, LifecycleStage.ImmediateDetached,
          ctx with { CurrentStage = LifecycleStage.ImmediateDetached }, default);
      } catch (OperationCanceledException) {
        // Graceful shutdown
#pragma warning disable RCS1075 // No logger in static context
      } catch (Exception) {
#pragma warning restore RCS1075
        // Errors surface via receptor telemetry
      }
    });
  }

  [LoggerMessage(Level = LogLevel.Error, Message = "Detached lifecycle stage {Stage} failed for message {MessageId}")]
  private static partial void LogDetachedStageError(ILogger logger, Exception ex, LifecycleStage stage, Guid? messageId);

  /// <summary>
  /// Logs a summary of perspective processing activity for the batch.
  /// </summary>
  private void _logBatchSummary(
      PerspectiveCursorCompletion[] completionsToSend,
      PerspectiveCursorFailure[] failuresToSend,
      WorkBatch workBatch) {

    int totalActivity = completionsToSend.Length + failuresToSend.Length + workBatch.PerspectiveWork.Count;
    if (totalActivity > 0) {
      LogPerspectiveBatchSummary(_logger, workBatch.PerspectiveWork.Count, completionsToSend.Length, failuresToSend.Length);
    } else {
      LogNoWorkClaimed(_logger);
    }
  }

  /// <summary>
  /// Tracks work state transitions for OnWorkProcessingStarted / OnWorkProcessingIdle callbacks.
  /// </summary>
  private void _updateWorkStateTracking(bool hasWork) {
    if (hasWork) {
      // Reset empty poll counter
      Interlocked.Exchange(ref _consecutiveEmptyPolls, 0);

      // Transition to active if was idle
      if (_isIdle) {
        _isIdle = false;
        OnWorkProcessingStarted?.Invoke();
        LogPerspectiveProcessingStarted(_logger);
      }
    } else {
      // Increment empty poll counter
      Interlocked.Increment(ref _consecutiveEmptyPolls);
      _metrics?.EmptyBatches.Add(1);

      // Check if should transition to idle
      if (!_isIdle && _consecutiveEmptyPolls >= _options.IdleThresholdPolls) {
        _isIdle = true;
        OnWorkProcessingIdle?.Invoke();
        LogPerspectiveProcessingIdle(_logger, _consecutiveEmptyPolls);
      }
    }
  }

  /// <summary>
  /// Starts a background keepalive task that periodically renews a stream lock.
  /// The task runs until the cancellation token is cancelled.
  /// </summary>
  private async Task _startLockKeepaliveAsync(Guid streamId, string perspectiveName, CancellationToken ct) {
    if (_streamLocker is null) {
      return;
    }
    try {
      while (!ct.IsCancellationRequested) {
        await Task.Delay(_streamLockOptions.KeepAliveInterval, ct);
        await _streamLocker.RenewLockAsync(streamId, perspectiveName, _instanceProvider.InstanceId, ct);
      }
    } catch (OperationCanceledException) {
      // Expected when the operation completes and keepalive is stopped
    }
  }

  /// <summary>
  /// Loads events that were just processed by the perspective run.
  /// Loads once and reuses for both PostPerspectiveDetached and PostPerspectiveInline stages.
  /// </summary>
  private async Task<List<MessageEnvelope<IEvent>>> _loadProcessedEventsAsync(
      IEventStore eventStore,
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      Guid currentEventId,
      CancellationToken cancellationToken) {

    if (_eventTypeProvider is null) {
      LogWarningNoEventTypes(_logger, perspectiveName, streamId);
      return [];
    }

    try {
      // Get all known event types from the provider (required for AOT-compatible polymorphic deserialization)
      var eventTypes = _eventTypeProvider.GetEventTypes();
      if (eventTypes.Count == 0) {
        LogWarningNoEventTypes(_logger, perspectiveName, streamId);
        return [];
      }

      // Load all events that were just processed by this perspective run
      // Use polymorphic read since we don't know the concrete event types ahead of time
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var eventTypesCount = eventTypes.Count;
        var lastProcessed = lastProcessedEventId.GetValueOrDefault();
        LogDiagnosticGetEventsBetween(_logger, perspectiveName, streamId, lastProcessed, currentEventId, eventTypesCount);
      }

      var processedEvents = await eventStore.GetEventsBetweenPolymorphicAsync(
        streamId,
        lastProcessedEventId,  // Exclusive start
        currentEventId,        // Inclusive end
        eventTypes,            // All known event types for deserialization
        cancellationToken
      );

      if (_logger.IsEnabled(LogLevel.Debug)) {
        var eventsCount = processedEvents.Count;
        LogDiagnosticGetEventsReturned(_logger, eventsCount, perspectiveName, streamId);
      }

      return processedEvents;

    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogErrorInvokingLifecycleReceptors(_logger, ex, perspectiveName, streamId);
      throw;
    }
  }

  /// <summary>
  /// Groups the replay-aware options (processing mode + per-event IsNew dictionary) that flow
  /// through lifecycle receptor invocation without inflating the parameter list.
  /// </summary>
  private readonly record struct LifecycleReplayOptions(
    ProcessingMode? ProcessingMode,
    IReadOnlyDictionary<Guid, bool>? IsNewByEventId) {
    public static LifecycleReplayOptions None => default;
  }

  /// <summary>
  /// Invokes lifecycle receptors for the given events at the specified stage.
  /// Used for both PostPerspectiveDetached (before checkpoint save) and PostPerspectiveInline (after checkpoint save).
  /// </summary>
  private async Task _invokeLifecycleReceptorsForEventsAsync(
      List<MessageEnvelope<IEvent>> processedEvents,
      PerspectiveStreamContext streamCtx,
      Type? perspectiveType,
      Guid currentEventId,
      LifecycleStage stage,
      LifecycleReplayOptions replayOptions,
      CancellationToken cancellationToken) {

    var scopedReceptorInvoker = streamCtx.ScopedProvider.GetService<IReceptorInvoker>();
    if (scopedReceptorInvoker is null) {
      // No receptor invoker registered — no receptors can fire, so nothing to do.
      // This is valid for minimal hosts (e.g., tests or schema-only tools) that wire
      // perspectives without the full dispatcher/receptor stack.
      return;
    }

    try {
      // Invoke receptors for each event. IsNewEvent defaults to true (live processing,
      // trigger events, and freshly-arrived post-rewind events are all "new"). The
      // rewind path overrides per-event via isNewByEventId when it replays already-
      // processed events for [ReceptorIdempotent(AlwaysFire = true)] receptors.
      foreach (var envelope in processedEvents) {
        var isNew = replayOptions.IsNewByEventId is null
          || !replayOptions.IsNewByEventId.TryGetValue(envelope.MessageId.Value, out var flag)
          || flag;
        var context = new LifecycleExecutionContext {
          CurrentStage = stage,
          StreamId = streamCtx.StreamId,
          PerspectiveType = perspectiveType,
          LastProcessedEventId = currentEventId,
          MessageSource = MessageSource.Local,
          AttemptNumber = 1,
          ProcessingMode = replayOptions.ProcessingMode,
          IsNewEvent = isNew
        };

        await _establishSecurityContextAsync(envelope, streamCtx.ScopedProvider, cancellationToken);

        await scopedReceptorInvoker.InvokeAsync(
          envelope,
          stage,
          context,
          cancellationToken
        );
      }

    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Log error but don't fail the entire perspective processing
      // Lifecycle receptor failures shouldn't prevent checkpoint progress
      LogErrorInvokingLifecycleReceptors(_logger, ex, streamCtx.PerspectiveName, streamCtx.StreamId);
      throw; // Never swallow exceptions
    }
  }

  /// <summary>
  /// Establishes security context from the envelope before lifecycle receptor invocation.
  /// Sets IScopeContextAccessor.Current and IMessageContextAccessor.Current.
  /// Same pattern as ReceptorInvoker for consistency.
  /// </summary>
  /// <docs>operations/workers/perspective-worker#security-context</docs>
  /// <tests>Whizbang.Core.Tests/Workers/PerspectiveWorkerSecurityContextTests.cs</tests>
  private static async ValueTask _establishSecurityContextAsync(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken) {

    // Hoist securityContext declaration so it can be used for MessageContext below
    IScopeContext? securityContext = null;

    // Establish security context from envelope (same pattern as ReceptorInvoker)
    var securityProvider = scopedProvider.GetService<IMessageSecurityContextProvider>();
    if (securityProvider is not null) {
      securityContext = await securityProvider
        .EstablishContextAsync(envelope, scopedProvider, cancellationToken)
        .ConfigureAwait(false);

      if (securityContext is not null) {
        var accessor = scopedProvider.GetService<IScopeContextAccessor>();
        if (accessor is not null) {
          accessor.Current = securityContext;
        }
      }
    }

    // Set message context with UserId and TenantId from scope context
    // FIX: Use extractor result first, fall back to envelope.GetCurrentScope()
    var scopeForMessageContext = securityContext ?? envelope.GetCurrentScope();

    // CRITICAL FIX: When extraction fails (securityContext is null) but envelope has scope,
    // we must:
    // 1. Wrap the scope in ImmutableScopeContext with ShouldPropagate=true so that
    //    CascadeContext.GetSecurityFromAmbient() can find it when lifecycle handlers append events
    // 2. Invoke callbacks manually so UserContextManagerCallback sets TenantContext
    if (securityContext is null && scopeForMessageContext is not null) {
      // Convert envelope scope to ImmutableScopeContext for propagation
      var extraction = new SecurityExtraction {
        Scope = scopeForMessageContext.Scope,
        Roles = scopeForMessageContext.Roles,
        Permissions = scopeForMessageContext.Permissions,
        SecurityPrincipals = scopeForMessageContext.SecurityPrincipals,
        Claims = scopeForMessageContext.Claims,
        ActualPrincipal = scopeForMessageContext.ActualPrincipal,
        EffectivePrincipal = scopeForMessageContext.EffectivePrincipal,
        ContextType = scopeForMessageContext.ContextType,
        Source = "EnvelopeHop"
      };
      var immutableScope = new ImmutableScopeContext(extraction, shouldPropagate: true);

      // Use the immutable scope for both accessor and message context
      scopeForMessageContext = immutableScope;

      // Set IScopeContextAccessor.Current with ImmutableScopeContext (required for GetSecurityFromAmbient)
      var accessor = scopedProvider.GetService<IScopeContextAccessor>();
      if (accessor is not null) {
        accessor.Current = immutableScope;
      }

      // Invoke callbacks with the immutable scope
      var callbacks = scopedProvider.GetServices<ISecurityContextCallback>();
      foreach (var callback in callbacks) {
        cancellationToken.ThrowIfCancellationRequested();
        await callback.OnContextEstablishedAsync(immutableScope, envelope, scopedProvider, cancellationToken)
          .ConfigureAwait(false);
      }
    }

    var messageContextAccessor = scopedProvider.GetService<IMessageContextAccessor>();
    if (messageContextAccessor is not null) {
      var messageContext = new MessageContext {
        MessageId = envelope.MessageId,
        CorrelationId = envelope.GetCorrelationId() ?? CorrelationId.New(),
        CausationId = envelope.GetCausationId() ?? MessageId.New(),
        Timestamp = envelope.GetMessageTimestamp(),
        UserId = scopeForMessageContext?.Scope?.UserId,
        TenantId = scopeForMessageContext?.Scope?.TenantId,
        ScopeContext = scopeForMessageContext
      };
      messageContextAccessor.Current = messageContext;

      // CRITICAL: Set InitiatingContext on IScopeContextAccessor (same pattern as ReceptorInvoker)
      // This establishes IMessageContext as the SOURCE OF TRUTH for security context.
      // Required for CascadeContext.GetSecurityFromAmbient() to find the scope when
      // lifecycle handlers append events via SecurityContextEventStoreDecorator.
      var scopeContextAccessor = scopedProvider.GetService<IScopeContextAccessor>();
      if (scopeContextAccessor is not null) {
        scopeContextAccessor.InitiatingContext = messageContext;
      }
    }
  }

  // LoggerMessage definitions
  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "Perspective worker starting: Instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}), interval: {Interval}ms"
  )]
  static partial void LogWorkerStarting(ILogger logger, Guid instanceId, string serviceName, string hostName, int processId, int interval);

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Debug,
    Message = "Checking for pending perspective cursors on startup..."
  )]
  static partial void LogCheckingPendingCheckpoints(ILogger logger);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Debug,
    Message = "Initial perspective cursor processing complete"
  )]
  static partial void LogInitialCheckpointProcessingComplete(ILogger logger);

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Error,
    Message = "Error processing perspective cursors"
  )]
  static partial void LogErrorProcessingCheckpoints(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Information,
    Message = "Perspective worker stopping"
  )]
  static partial void LogWorkerStopping(ILogger logger);

  [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Information,
    Message = "Processing perspective cursor: {PerspectiveName} for stream {StreamId}, last processed event: {LastProcessedEventId}"
  )]
  static partial void LogProcessingPerspectiveCursor(ILogger logger, string perspectiveName, Guid streamId, string lastProcessedEventId);

  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Error,
    Message = "IPerspectiveRunnerRegistry not registered. Call AddPerspectiveRunners() in service registration. Skipping perspective: {PerspectiveName}"
  )]
  static partial void LogPerspectiveRunnerRegistryNotRegistered(ILogger logger, string perspectiveName);

  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Warning,
    Message = "No IPerspectiveRunner found for perspective '{PerspectiveName}' (stream: {StreamId}). See startup log for registered perspectives."
  )]
  static partial void LogNoPerspectiveRunnerFound(ILogger logger, string perspectiveName, Guid streamId);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Debug,
    Message = "Perspective checkpoint completed: {PerspectiveName} for stream {StreamId}, last event: {LastEventId}"
  )]
  static partial void LogPerspectiveCursorCompleted(ILogger logger, string perspectiveName, Guid streamId, Guid lastEventId);

  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Error,
    Message = "Error processing perspective cursor: {PerspectiveName} for stream {StreamId}"
  )]
  static partial void LogErrorProcessingPerspectiveCursor(ILogger logger, Exception ex, string perspectiveName, Guid streamId);

  [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Information,
    Message = "Perspective batch: Claimed={Claimed}, completed={Completed}, failed={Failed}"
  )]
  static partial void LogPerspectiveBatchSummary(ILogger logger, int claimed, int completed, int failed);

  [LoggerMessage(
    EventId = 16,
    Level = LogLevel.Debug,
    Message = "Perspective checkpoint processing: no work claimed"
  )]
  static partial void LogNoWorkClaimed(ILogger logger);

  [LoggerMessage(
    EventId = 17,
    Level = LogLevel.Debug,
    Message = "Perspective processing started (idle → active)"
  )]
  static partial void LogPerspectiveProcessingStarted(ILogger logger);

  [LoggerMessage(
    EventId = 18,
    Level = LogLevel.Debug,
    Message = "Perspective processing idle (active → idle) after {EmptyPolls} empty polls"
  )]
  static partial void LogPerspectiveProcessingIdle(ILogger logger, int emptyPolls);

  [LoggerMessage(
    EventId = 19,
    Level = LogLevel.Error,
    Message = "Error processing work batch (database failure - completions will retry)"
  )]
  static partial void LogErrorProcessingWorkBatch(ILogger logger, Exception ex);

  /// <summary>
  /// Diagnostic log entry for tracing runner registry resolution.
  /// Used to debug DI container isolation issues where multiple services share the same host.
  /// HashCode helps verify that each service resolves its own registry instance.
  /// </summary>
  [LoggerMessage(
    EventId = 20,
    Level = LogLevel.Debug,
    Message = "DIAGNOSTIC: Resolved runner registry for perspective '{PerspectiveName}': Type={RegistryType}, HashCode={RegistryHashCode}"
  )]
  static partial void LogRunnerRegistryResolved(ILogger logger, string perspectiveName, string registryType, int registryHashCode);

  /// <summary>
  /// Diagnostic log entry for tracing runner instance resolution.
  /// Used to debug scenarios where the wrong service's runner is used for perspective processing.
  /// HashCode helps verify that the correct runner instance is resolved for the current service.
  /// </summary>
  [LoggerMessage(
    EventId = 21,
    Level = LogLevel.Debug,
    Message = "DIAGNOSTIC: Resolved runner instance for perspective '{PerspectiveName}': Type={RunnerType}, HashCode={RunnerHashCode}"
  )]
  static partial void LogRunnerInstanceResolved(ILogger logger, string perspectiveName, string runnerType, int runnerHashCode);

  /// <summary>
  /// Error invoking lifecycle receptors after perspective processing.
  /// Lifecycle receptor failures are logged but don't prevent checkpoint progress.
  /// </summary>
  [LoggerMessage(
    EventId = 22,
    Level = LogLevel.Error,
    Message = "Error invoking lifecycle receptors for perspective {PerspectiveName} on stream {StreamId}"
  )]
  static partial void LogErrorInvokingLifecycleReceptors(ILogger logger, Exception ex, string perspectiveName, Guid streamId);

  [LoggerMessage(
    EventId = 23,
    Level = LogLevel.Warning,
    Message = "No event types available from IEventTypeProvider for perspective {PerspectiveName} on stream {StreamId}. Skipping lifecycle receptor invocation."
  )]
  static partial void LogWarningNoEventTypes(ILogger logger, string perspectiveName, Guid streamId);

  /// <summary>
  /// Diagnostic log entry for debugging lifecycle invocation dependencies.
  /// Helps diagnose why PostPerspective lifecycle stages might not be firing.
  /// </summary>
  [LoggerMessage(
    EventId = 24,
    Level = LogLevel.Debug,
    Message = "DIAGNOSTIC: Lifecycle dependencies for perspective '{PerspectiveName}' on stream {StreamId}: LifecycleInvoker={HasLifecycleInvoker}, EventStore={HasEventStore}, EventTypeProvider={HasEventTypeProvider}"
  )]
  static partial void LogLifecycleDependenciesResolved(ILogger logger, string perspectiveName, Guid streamId, bool hasLifecycleInvoker, bool hasEventStore, bool hasEventTypeProvider);

  /// <summary>
  /// Debug log for reporting perspective completion to coordinator.
  /// Traces when checkpoint is about to be saved to database.
  /// </summary>
  [LoggerMessage(
    EventId = 25,
    Level = LogLevel.Debug,
    Message = "[PerspectiveWorker] Reporting completion for {PerspectiveName} on stream {StreamId}, lastEventId={LastEventId}"
  )]
  static partial void LogReportingCompletion(ILogger logger, string perspectiveName, Guid streamId, Guid lastEventId);

  /// <summary>
  /// Debug log for successful completion report.
  /// Confirms checkpoint was saved to database via completion strategy.
  /// </summary>
  [LoggerMessage(
    EventId = 26,
    Level = LogLevel.Debug,
    Message = "[PerspectiveWorker] Completion reported successfully"
  )]
  static partial void LogCompletionReported(ILogger logger);

  /// <summary>
  /// Debug log for checking PostPerspectiveInline preconditions.
  /// Shows whether conditions are met for invoking PostPerspectiveInline lifecycle stage.
  /// </summary>
  [LoggerMessage(
    EventId = 27,
    Level = LogLevel.Debug,
    Message = "[PerspectiveWorker] Checking PostPerspectiveInline: processedEvents.Count={EventCount}, lifecycleInvoker={HasInvoker}"
  )]
  static partial void LogCheckingPostPerspectiveInline(ILogger logger, int eventCount, bool hasInvoker);

  /// <summary>
  /// Debug log for invoking PostPerspectiveInline receptors.
  /// Critical for test synchronization - fires AFTER checkpoint is saved.
  /// </summary>
  [LoggerMessage(
    EventId = 28,
    Level = LogLevel.Debug,
    Message = "[PerspectiveWorker] Invoking PostPerspectiveInline for {EventCount} events on {PerspectiveName}/{StreamId}"
  )]
  static partial void LogInvokingPostPerspectiveInline(ILogger logger, int eventCount, string perspectiveName, Guid streamId);

  /// <summary>
  /// Debug log for successful PostPerspectiveInline completion.
  /// Confirms all blocking lifecycle receptors have finished.
  /// </summary>
  [LoggerMessage(
    EventId = 29,
    Level = LogLevel.Debug,
    Message = "[PerspectiveWorker] PostPerspectiveInline invocation completed"
  )]
  static partial void LogPostPerspectiveInlineCompleted(ILogger logger);

  /// <summary>
  /// Debug log explaining why PostPerspectiveInline was skipped (no processed events).
  /// </summary>
  [LoggerMessage(
    EventId = 30,
    Level = LogLevel.Debug,
    Message = "[PerspectiveWorker] Skipping PostPerspectiveInline: no processed events"
  )]
  static partial void LogSkippingPostPerspectiveInlineNoEvents(ILogger logger);

  /// <summary>
  /// Debug log explaining why PostPerspectiveInline was skipped (no lifecycle invoker).
  /// </summary>
  [LoggerMessage(
    EventId = 31,
    Level = LogLevel.Debug,
    Message = "[PerspectiveWorker] Skipping PostPerspectiveInline: no lifecycle invoker registered"
  )]
  static partial void LogSkippingPostPerspectiveInlineNoInvoker(ILogger logger);

  /// <summary>
  /// DIAGNOSTIC: Log which service is processing work batch (service name maps to schema).
  /// </summary>
  [LoggerMessage(
    EventId = 32,
    Level = LogLevel.Debug,
    Message = "[PerspectiveWorker SCHEMA DIAGNOSTIC] Service={ServiceName} (InstanceId={InstanceId}) is processing checkpoints"
  )]
  static partial void LogProcessingWorkBatchForService(ILogger logger, string serviceName, Guid instanceId);

  /// <summary>
  /// Logs the header line indicating how many perspectives are registered at startup.
  /// </summary>
  [LoggerMessage(
    EventId = 33,
    Level = LogLevel.Information,
    Message = "Registered {Count} perspective(s):"
  )]
  static partial void LogRegisteredPerspectivesHeader(ILogger logger, int count);

  /// <summary>
  /// Logs details of a single registered perspective at startup.
  /// Shows CLR type name, model type, number of event handlers, and event type names.
  /// </summary>
  [LoggerMessage(
    EventId = 34,
    Level = LogLevel.Information,
    Message = "  - {PerspectiveName} (Model: {ModelType}, Events: {EventCount}) [{EventTypes}]"
  )]
  static partial void LogRegisteredPerspective(ILogger logger, string perspectiveName, string modelType, int eventCount, string eventTypes);

  /// <summary>
  /// Logs when no perspectives are registered at startup (potential configuration issue).
  /// </summary>
  [LoggerMessage(
    EventId = 35,
    Level = LogLevel.Warning,
    Message = "No perspectives registered. Ensure AddPerspectiveRunners() is called during service registration."
  )]
  static partial void LogNoPerspectivesRegistered(ILogger logger);

  /// <summary>
  /// Logs when IPerspectiveRunnerRegistry is not available at startup.
  /// </summary>
  [LoggerMessage(
    EventId = 36,
    Level = LogLevel.Debug,
    Message = "IPerspectiveRunnerRegistry not available at startup (perspectives may be registered lazily)"
  )]
  static partial void LogPerspectiveRegistryNotAvailableAtStartup(ILogger logger);

  // Diagnostic logging - Debug level only
#pragma warning disable S107 // LoggerMessage-generated method — parameter count cannot be reduced
  [LoggerMessage(
    EventId = 37,
    Level = LogLevel.Debug,
    Message = "[DIAGNOSTIC] Loading events for {PerspectiveName}/{StreamId}: shouldLoad={ShouldLoad}, invoker={HasInvoker}, store={HasStore}, status={Status}, lastProcessed={LastProcessed}, current={Current}"
  )]
  static partial void LogDiagnosticLoadingEvents(ILogger logger, string perspectiveName, Guid streamId, bool shouldLoad, bool hasInvoker, bool hasStore, string status, Guid lastProcessed, Guid current);
#pragma warning restore S107

  [LoggerMessage(
    EventId = 38,
    Level = LogLevel.Debug,
    Message = "[DIAGNOSTIC] Loaded {Count} events for {PerspectiveName}/{StreamId}"
  )]
  static partial void LogDiagnosticLoadedEvents(ILogger logger, int count, string perspectiveName, Guid streamId);

  [LoggerMessage(
    EventId = 39,
    Level = LogLevel.Debug,
    Message = "[DIAGNOSTIC] Skipping PostPerspectiveInline for {PerspectiveName}/{StreamId}: NO EVENTS (lastProcessed={LastProcessed}, current={Current})"
  )]
  static partial void LogDiagnosticNoEvents(ILogger logger, string perspectiveName, Guid streamId, Guid lastProcessed, Guid current);

  [LoggerMessage(
    EventId = 40,
    Level = LogLevel.Debug,
    Message = "[DIAGNOSTIC] Skipping PostPerspectiveInline for {PerspectiveName}/{StreamId}: NO INVOKER"
  )]
  static partial void LogDiagnosticNoInvoker(ILogger logger, string perspectiveName, Guid streamId);

  [LoggerMessage(
    EventId = 41,
    Level = LogLevel.Debug,
    Message = "[DIAGNOSTIC] Calling GetEventsBetweenPolymorphicAsync for {PerspectiveName}/{StreamId}: lastProcessed={LastProcessed}, current={Current}, eventTypes={EventTypesCount}"
  )]
  static partial void LogDiagnosticGetEventsBetween(ILogger logger, string perspectiveName, Guid streamId, Guid lastProcessed, Guid current, int eventTypesCount);

  [LoggerMessage(
    EventId = 42,
    Level = LogLevel.Debug,
    Message = "[DIAGNOSTIC] GetEventsBetweenPolymorphicAsync returned {Count} events for {PerspectiveName}/{StreamId}"
  )]
  static partial void LogDiagnosticGetEventsReturned(ILogger logger, int count, string perspectiveName, Guid streamId);

  [LoggerMessage(
    EventId = 43,
    Level = LogLevel.Warning,
    Message = "Failed to acquire stream lock for rewind on {PerspectiveName} stream {StreamId}, deferring"
  )]
  static partial void LogFailedToAcquireRewindLock(ILogger logger, string perspectiveName, Guid streamId);

  [LoggerMessage(
    EventId = 44,
    Level = LogLevel.Debug,
    Message = "Dedup: skipped {SkippedCount} already-processed work items out of {TotalCount}"
  )]
  static partial void LogDedupSkipped(ILogger logger, int skippedCount, int totalCount);

  /// <summary>
  /// Filters out work items whose WorkIds are already in the processed event cache.
  /// Notifies the observer for each group of deduped items.
  /// </summary>
  private List<PerspectiveWork> _filterDuplicateWorkItems(List<PerspectiveWork> workItems) {
    if (workItems.Count == 0) {
      return workItems;
    }

    var dedupedWork = new List<PerspectiveWork>(workItems.Count);
    var skippedWork = new List<PerspectiveWork>();

    foreach (var item in workItems) {
      if (_processedEventCache.Contains(item.WorkId)) {
        skippedWork.Add(item);
      } else {
        dedupedWork.Add(item);
      }
    }

    // Notify observer for each group of deduped items
    if (skippedWork.Count > 0) {
      foreach (var g in skippedWork.GroupBy(w => new { w.StreamId, w.PerspectiveName })) {
        var skippedIds = g.Select(w => w.WorkId).ToList();
        _processedEventCache.Observer.OnEventsDeduped(skippedIds, g.Key.PerspectiveName, g.Key.StreamId);
      }

      LogDedupSkipped(_logger, skippedWork.Count, workItems.Count);
    }

    return dedupedWork;
  }

  [LoggerMessage(
    EventId = 45,
    Level = LogLevel.Warning,
    Message = "Event type key '{EventTypeKey}' not found in perspective registry. PostAllPerspectives/PostLifecycle will fire without WhenAll gate."
  )]
  static partial void LogEventTypeNotInPerspectiveRegistry(ILogger logger, string eventTypeKey);

  [LoggerMessage(
    EventId = 46,
    Level = LogLevel.Information,
    Message = "Cleaned {Count} stale lifecycle tracking entries (inactive > 5 minutes)"
  )]
  static partial void LogStaleTrackingCleaned(ILogger logger, int count);

  [LoggerMessage(
    EventId = 47,
    Level = LogLevel.Error,
    Message = "PostLifecycle stage failed for event {EventId}. Error isolated — other events continue processing."
  )]
  static partial void LogPostLifecycleError(ILogger logger, Exception exception, Guid eventId);

  [LoggerMessage(
    EventId = 48,
    Level = LogLevel.Information,
    Message = "Reconciliation starting: {Count} orphaned lifecycle events found"
  )]
  static partial void LogReconciliationStarting(ILogger logger, int count);

  [LoggerMessage(
    EventId = 49,
    Level = LogLevel.Information,
    Message = "Reconciliation completed for event {EventId}"
  )]
  static partial void LogReconciliationCompleted(ILogger logger, Guid eventId);

  [LoggerMessage(
    EventId = 50,
    Level = LogLevel.Error,
    Message = "Reconciliation failed for event {EventId}. Error isolated — other events continue."
  )]
  static partial void LogReconciliationError(ILogger logger, Exception exception, Guid eventId);

  [LoggerMessage(
    EventId = 51,
    Level = LogLevel.Error,
    Message = "Lifecycle reconciliation scan failed. Will retry on next startup."
  )]
  static partial void LogReconciliationFailed(ILogger logger, Exception exception);

  [LoggerMessage(
    EventId = 52,
    Level = LogLevel.Warning,
    Message = "Perspective rewind required for {PerspectiveName} stream {StreamId} — cursor at {CursorEventId}, late event {TriggerEventId} ({EventsBehind} events behind)"
  )]
  static partial void LogRewindRequired(ILogger logger, string perspectiveName, Guid streamId, Guid cursorEventId, Guid triggerEventId, int eventsBehind);

  [LoggerMessage(
    EventId = 53,
    Level = LogLevel.Warning,
    Message = "Perspective rewind completed for {PerspectiveName} stream {StreamId} — replayed {EventsReplayed} events in {DurationMs}ms (from {ReplaySource})"
  )]
  static partial void LogRewindCompleted(ILogger logger, string perspectiveName, Guid streamId, int eventsReplayed, long durationMs, string replaySource);

  [LoggerMessage(
    EventId = 58,
    Level = LogLevel.Error,
    Message = "Perspective rewind failed for {PerspectiveName} stream {StreamId} — trigger event {TriggerEventId}. Stream will retry on next cycle."
  )]
  static partial void LogRewindFailed(ILogger logger, Exception exception, string perspectiveName, Guid streamId, Guid triggerEventId);

  [LoggerMessage(
    EventId = 59,
    Level = LogLevel.Warning,
    Message = "Prior-cycle PostLifecycle faulted; current cycle continues. Stage guards ensure exactly-once semantics for any events that completed stages before the fault."
  )]
  static partial void LogPriorPostLifecycleFaulted(ILogger logger, Exception exception);

  [LoggerMessage(
    EventId = 60,
    Level = LogLevel.Debug,
    Message = "Drain cycle complete: drainMode={DrainModeActive} eventsProcessed={EventsProcessed} cycleDurationMs={CycleDurationMs}"
  )]
  static partial void LogDrainCycleComplete(ILogger logger, bool drainModeActive, int eventsProcessed, long cycleDurationMs);
}

/// <summary>
/// Log messages for perspective startup rewind scan.
/// Separate category from PerspectiveWorker so log level can be configured independently.
/// Configure via: "Whizbang.Core.Workers.PerspectiveStartupScan": "Information"
/// </summary>
/// <docs>fundamentals/perspectives/rewind#startup-scan</docs>
internal static partial class PerspectiveStartupScanLog {
  [LoggerMessage(
    EventId = 54,
    Level = LogLevel.Information,
    Message = "Startup rewind scan started: {StreamCount} streams require rewind across {PerspectiveCount} perspectives"
  )]
  internal static partial void LogStartupRewindScanStarted(ILogger logger, int streamCount, int perspectiveCount);

  [LoggerMessage(
    EventId = 55,
    Level = LogLevel.Information,
    Message = "Startup rewind scan completed: {StreamCount} streams, {PerspectiveCount} perspectives rewound in {DurationMs}ms"
  )]
  internal static partial void LogStartupRewindScanCompleted(ILogger logger, int streamCount, int perspectiveCount, long durationMs);

  [LoggerMessage(
    EventId = 57,
    Level = LogLevel.Information,
    Message = "Startup rewind scan: no streams require rewind"
  )]
  internal static partial void LogStartupRewindScanClean(ILogger logger);

  [LoggerMessage(
    EventId = 56,
    Level = LogLevel.Warning,
    Message = "Error during startup rewind scan — rewinds will be processed during normal polling"
  )]
  internal static partial void LogStartupRewindScanError(ILogger logger, Exception exception);

}

/// <summary>
/// Configuration options for the Perspective worker.
/// </summary>
public class PerspectiveWorkerOptions {
  /// <summary>
  /// Milliseconds to wait between polling for perspective cursor work.
  /// Default: 1000 (1 second)
  /// </summary>
  public int PollingIntervalMilliseconds { get; set; } = 1000;

  /// <summary>
  /// Lease duration in seconds.
  /// Perspective cursors claimed will be locked for this duration.
  /// Default: 300 (5 minutes)
  /// </summary>
  public int LeaseSeconds { get; set; } = 300;

  /// <summary>
  /// Grace period before a non-heartbeating instance is abandoned, in seconds (default: 30).
  /// After this, the instance's message leases are released and its stream ownership no longer
  /// blocks other instances from claiming fresh work. See
  /// <see cref="WorkCoordinatorPublisherOptions.AbandonStaleInstanceThresholdSeconds"/> for
  /// the full rationale and tuning guidance.
  /// </summary>
  public int AbandonStaleInstanceThresholdSeconds { get; set; } = 30;

  /// <summary>
  /// Optional metadata to attach to this service instance.
  /// Can include version, environment, etc.
  /// Supports any JSON value type via JsonElement.
  /// </summary>
  public Dictionary<string, JsonElement>? InstanceMetadata { get; set; }

  /// <summary>
  /// Keep completed checkpoints for debugging (default: false).
  /// When enabled, completed checkpoints are preserved instead of deleted.
  /// </summary>
  public bool DebugMode { get; set; }

  /// <summary>
  /// Number of partitions for work distribution.
  /// Default: 10000
  /// </summary>
  public int PartitionCount { get; set; } = 10_000;

  /// <summary>
  /// Number of consecutive empty work polls required to trigger OnWorkProcessingIdle callback.
  /// Default: 2
  /// </summary>
  public int IdleThresholdPolls { get; set; } = 2;

  /// <summary>
  /// Number of events to process in a single batch before saving model + checkpoint.
  /// Higher values = fewer database writes but longer transactions.
  /// Lower values = more frequent saves but higher DB overhead.
  /// Default: 100
  /// </summary>
  public int PerspectiveBatchSize { get; set; } = 100;

  /// <summary>
  /// Maximum number of perspective groups to process concurrently within a single batch.
  /// Higher values improve throughput when multiple perspectives/streams have pending work.
  /// Different (streamId, perspectiveName) pairs are independent and can safely run in parallel.
  /// Default: 30
  /// </summary>
  public int MaxConcurrentPerspectives { get; set; } = 30;

  /// <summary>
  /// Slice 17: number of parallel consumer loops running on the channel reader. Each loop
  /// independently waits for work, builds a batch, and runs <c>ProcessChannelBatchAsync</c>
  /// (which itself parallelizes per-stream-perspective up to <see cref="MaxConcurrentPerspectives"/>).
  /// Outer × inner gives the steady-state concurrency ceiling. Default 4 (so ~120 concurrent
  /// stream-perspective applies at peak).
  /// <para>
  /// Sized to break the single-consumer batch loop bottleneck observed on JDX BFF where
  /// saga fan-out enqueued perspective work faster than one consumer could drain (38/sec
  /// drain vs ~180/sec arrivals). Set to 1 to restore the pre-slice-17 single-consumer
  /// behavior.
  /// </para>
  /// </summary>
  public int MaxConcurrentDrainConsumers { get; set; } = 4;

  /// <summary>
  /// Maximum number of streams to return per batch from the SQL function.
  /// Controls how many distinct streams are claimed and processed per tick.
  /// Default: 300
  /// </summary>
  public int MaxStreamsPerBatch { get; set; } = 300;

  /// <summary>
  /// Slice 30 — caps the per-stream drain loop introduced to amortize per-drain envelope
  /// overhead (DI scope + LeaseHandle + BackgroundStageDispatch OS thread) across events
  /// that arrive from the transport DURING an in-progress drain. After the first iteration
  /// processes the initially-fetched batch, the loop refetches the stream and runs again if
  /// more events came back; cooldown filters already-processed rows so the next iteration
  /// only runs for genuinely fresh work. The cap bounds latency for streams that receive a
  /// sustained high arrival rate — when hit, the partially-processed remainder is picked up
  /// by ClaimWorker on the next tick. Set to 1 to disable the loop and restore pre-slice-30
  /// single-pass behavior.
  /// <para>
  /// Sized from JDX run 21 PERF data: 22 318 single-event drains × ~150 ms envelope cost
  /// dominated the import wall time. Each loop iteration is a single SQL refetch (~5-10 ms)
  /// plus the per-perspective apply path; even at the cap (5) the loop costs at most a few
  /// hundred ms per stream vs the multi-second savings from skipping 5× drain envelopes.
  /// </para>
  /// </summary>
  public int DrainLoopMaxIterations { get; set; } = 5;

  /// <summary>
  /// Slice 30 — minimum batch size that triggers a refetch in the per-stream drain loop.
  /// When the current iteration processed fewer events than this threshold, the loop exits
  /// without refetching to avoid wasting ~5-10 ms of SQL on the steady-state low-arrival
  /// case (single-event drains, which dominated JDX run 21: 22 318 of ~30 000 total drains).
  /// Set to 1 to refetch unconditionally; set above <see cref="DrainLoopMaxIterations"/> to
  /// disable the loop entirely (functionally identical to <c>DrainLoopMaxIterations = 1</c>).
  /// </summary>
  public int DrainLoopRefetchMinBatch { get; set; } = 2;

  /// <summary>
  /// Sliding-window batching policy for stream_id signals from the perspective drain
  /// channel — this IS the apply-batching window the user-facing
  /// <see cref="Messaging.IApplyBatchStrategy"/> interface advertises. After the first
  /// signal arrives, the worker waits up to
  /// <see cref="SlidingWindowBatcherOptions.SlidingWindow"/> for additional signals
  /// before fetching events — letting more arrivals accumulate so the per-stream
  /// fetch returns a coherent in-order chunk and ONE apply cycle processes everything
  /// pending. Bounded by <see cref="SlidingWindowBatcherOptions.MaxWait"/> and
  /// <see cref="SlidingWindowBatcherOptions.MaxSize"/>. The work channel (legacy
  /// <see cref="PerspectiveWork"/> items) is read in the same WhenAny loop but is
  /// NOT batched — the dedup tests assert per-cycle work-item processing semantics.
  /// </summary>
  /// <remarks>
  /// Defaults: 300 ms debounce / 3 s hard cap / 1000 signal ceiling. Tuned for the JDX
  /// bulk-import case where a single stream (e.g. <c>UberDraftJob</c>) receives ~46
  /// events in rapid succession — without the longer window, each event triggers a
  /// separate apply cycle. With 300 ms the window almost always coalesces all 46
  /// into one apply pass.
  /// </remarks>
  /// <docs>fundamentals/perspectives/drain-mode#sliding-window</docs>
  public SlidingWindowBatcherOptions DrainBatcher { get; set; } = new() {
    SlidingWindow = TimeSpan.FromMilliseconds(300),
    MaxWait = TimeSpan.FromSeconds(3),
    MaxSize = 1000,
  };

  /// <summary>
  /// Retry configuration for completion acknowledgement.
  /// Controls exponential backoff when ProcessWorkBatchAsync fails.
  /// </summary>
  public WorkerRetryOptions RetryOptions { get; set; } = new();
}
