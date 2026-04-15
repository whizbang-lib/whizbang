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
  IDatabaseReadinessCheck? databaseReadinessCheck = null,
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
  IOptions<PerspectiveRewindOptions>? rewindOptions = null
) : BackgroundService {
#pragma warning restore S107
  private readonly ConcurrentBag<Task> _detachedTasks = [];
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IDatabaseReadinessCheck _databaseReadinessCheck = databaseReadinessCheck ?? new DefaultDatabaseReadinessCheck();
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
  private readonly ILogger _startupScanLogger = scopeFactory.CreateScope().ServiceProvider
    .GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Workers.PerspectiveStartupScan")
    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
  private readonly IPerspectiveStreamLocker? _streamLocker = streamLocker;
  private readonly PerspectiveStreamLockOptions _streamLockOptions = streamLockOptions?.Value ?? new PerspectiveStreamLockOptions();

  // Perspective event completions (WorkIds to delete from wh_perspective_events)
  // Used by legacy PerspectiveWork path; drain mode uses per-stream immediate completion instead.
  private readonly System.Collections.Concurrent.ConcurrentQueue<PerspectiveEventCompletion> _pendingEventCompletions = new();

  // Drain mode watch-list: StreamId → cooldown counter.
  // Active streams (with events) have cooldown = 0.
  // Streams with no events get cooldown set to WatchListCooldownCycles, decremented each cycle.
  // Removed from watch-list when cooldown reaches 0 after decrement.
  private readonly Dictionary<Guid, int> _watchList = new();

  // Persistent PostLifecycle tracking: events stay until WhenAll resolves and PostLifecycle fires.
  // Carries over across ticks so events that fail WhenAll in tick N get re-checked in tick N+1.
  private readonly ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> _pendingPostLifecycleEvents = new();

  // Cursor cache: avoids GetPerspectiveCursorAsync DB call on every drain cycle.
  // Updated after each successful RunWithEventsAsync. Invalidated on rewind/rebuild/watch-list removal.
  private readonly PerspectiveCursorCache _cursorCache = new();

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

  // Metrics tracking
  private int _consecutiveDatabaseNotReadyChecks;
  private int _consecutiveEmptyPolls;
  private bool _isIdle = true;  // Start in idle state
  private int _batchCycleCount;

  // Wake signal: allows external callers to interrupt the polling delay
  // so the worker processes new perspective events immediately.
  private readonly SemaphoreSlim _pollWakeSignal = new(0, 1);
  private int _wakeSignaled;  // Guard to prevent SemaphoreFullException on redundant wake calls

  /// <summary>
  /// Gets the number of consecutive times the database was not ready.
  /// Resets to 0 when database becomes ready.
  /// </summary>
  public int ConsecutiveDatabaseNotReadyChecks => _consecutiveDatabaseNotReadyChecks;

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

    try {
      await _initializePerspectiveRegistryAsync();
      await _processInitialCheckpointsAsync(stoppingToken);
      await _reconcileOrphanedLifecyclesAsync(stoppingToken);
      await _scanAndRepairRewindsOnStartupAsync(stoppingToken);
    } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) {
      // Host shutting down while startup methods were running (e.g. port bind failure disposed
      // the DI container before the background service finished initialising). Exit gracefully.
      return;
    }

    // Subscribe to new perspective work signals so we poll immediately when events arrive
    if (workChannelWriter is not null) {
      workChannelWriter.OnNewPerspectiveWorkAvailable += RequestImmediatePoll;
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        if (!await _checkDatabaseReadinessAsync(stoppingToken)) {
          await _pollWakeSignal.WaitAsync(TimeSpan.FromMilliseconds(_options.PollingIntervalMilliseconds), stoppingToken);
          Interlocked.Exchange(ref _wakeSignaled, 0);
          continue;
        }

        var assignmentCount = await _processWorkBatchAsync(stoppingToken);
        _periodicStaleTrackingCleanup();
        await _periodicGatherStatisticsAsync(stoppingToken);

        // Drain mode: if the tick returned a significant backlog, loop immediately.
        // For trickle work (< 5 assignments), use normal polling to avoid thread starvation.
        // Drain mode disabled: always use normal polling to avoid thread starvation.
        // The perspective processing speed comes from MaxConcurrentPerspectives=30
        // and batch fetch, not from eliminating the 1-second sleep.
      } catch (ObjectDisposedException) {
        break;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogErrorProcessingCheckpoints(_logger, ex);
        throw; // Never swallow exceptions
      }

      try {
        // Wait for either the polling interval OR an external wake signal (whichever comes first).
        // RequestImmediatePoll() releases the semaphore, waking this loop early.
        await _pollWakeSignal.WaitAsync(TimeSpan.FromMilliseconds(_options.PollingIntervalMilliseconds), stoppingToken);
        Interlocked.Exchange(ref _wakeSignaled, 0);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogWorkerStopping(_logger);
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

  private async Task _processInitialCheckpointsAsync(CancellationToken stoppingToken) {
    try {
      LogCheckingPendingCheckpoints(_logger);
      var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
      if (isDatabaseReady) {
        await _processWorkBatchAsync(stoppingToken);
        LogInitialCheckpointProcessingComplete(_logger);
      } else {
        LogDatabaseNotReadyOnStartup(_logger);
      }
    } catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException) {
      LogErrorProcessingInitialCheckpoints(_logger, ex);
      throw; // Never swallow exceptions
    }
  }

  private async Task<bool> _checkDatabaseReadinessAsync(CancellationToken stoppingToken) {
    var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
    if (isDatabaseReady) {
      Interlocked.Exchange(ref _consecutiveDatabaseNotReadyChecks, 0);
      return true;
    }

    Interlocked.Increment(ref _consecutiveDatabaseNotReadyChecks);
    LogDatabaseNotReady(_logger, _consecutiveDatabaseNotReadyChecks);
    if (_consecutiveDatabaseNotReadyChecks > 10) {
      LogDatabaseNotReadyWarning(_logger, _consecutiveDatabaseNotReadyChecks);
    }
    return false;
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
      if (!await _databaseReadinessCheck.IsReadyAsync(ct)) {
        return;
      }

      // Query cursors with RewindRequired flag
      await using var scope = _scopeFactory.CreateAsyncScope();
      var workCoordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (workCoordinator is null) {
        return;
      }

      var rewindCursors = await workCoordinator.GetCursorsRequiringRewindAsync(ct);
      if (rewindCursors.Count == 0) {
        PerspectiveStartupScanLog.LogStartupRewindScanClean(_startupScanLogger);
        return;
      }

      var streamCount = rewindCursors.Select(c => c.StreamId).Distinct().Count();
      var perspectiveCount = rewindCursors.Count;
      PerspectiveStartupScanLog.LogStartupRewindScanStarted(_startupScanLogger, streamCount, perspectiveCount);

      if (_rewindOptions.StartupRewindMode == RewindStartupMode.Blocking) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Keep processing work batches until all rewinds are done
        var maxIterations = 100;  // Safety limit
        for (var i = 0; i < maxIterations; i++) {
          await _processWorkBatchAsync(ct);

          // Re-check
          rewindCursors = await workCoordinator.GetCursorsRequiringRewindAsync(ct);
          if (rewindCursors.Count == 0) {
            break;
          }
        }
        PerspectiveStartupScanLog.LogStartupRewindScanCompleted(_startupScanLogger, streamCount, perspectiveCount, (long)sw.Elapsed.TotalMilliseconds);
      }
      // Background mode: normal polling loop will pick them up — individual rewinds log via PerspectiveWorker
    } catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException) {
      PerspectiveStartupScanLog.LogStartupRewindScanError(_startupScanLogger, ex);
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

  // S3776: Core perspective processing pipeline — inherent complexity from 5-phase lifecycle (claim/apply/buffer/postPerspective/postLifecycle)
#pragma warning disable S3776
  private async Task<int> _processWorkBatchAsync(CancellationToken cancellationToken) {
#pragma warning restore S3776
    var batchSw = System.Diagnostics.Stopwatch.StartNew();

    // Capture parent context BEFORE making span decisions
    // This ensures child spans can find a parent even when intermediate spans are skipped
    // On background threads, Activity.Current is typically null unless explicitly set
    var parentContext = Activity.Current?.Context ?? default;

    // Optionally create parent activity for all perspective processing in this batch
    // When enabled, all child activities (Perspective {name}, Lifecycle stages) will be parented to this
    // Controlled by TracingOptions.EnableWorkerBatchSpans (default: false to reduce noise)
    var enableBatchSpan = _tracingOptions?.CurrentValue.EnableWorkerBatchSpans ?? false;
    using var batchActivity = enableBatchSpan
      ? WhizbangActivitySource.Tracing.StartActivity("PerspectiveWorker ProcessBatch", ActivityKind.Internal)
      : null;
    if (batchActivity is not null) {
      batchActivity.SetTag("whizbang.worker", "PerspectiveWorker");
      batchActivity.SetTag("whizbang.service.name", _instanceProvider.ServiceName);
      batchActivity.SetTag("whizbang.instance.id", _instanceProvider.InstanceId.ToString());
    }

    // Compute effective parent: use batch span if created, otherwise use captured parent
    // This cascades parent context even when batch span is disabled
    var effectiveParent = batchActivity?.Context ?? parentContext;

    // Create a scope to resolve scoped IWorkCoordinator and IReceptorInvoker
    await using var scope = _scopeFactory.CreateAsyncScope();
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
    var lifecycleCoordinator = scope.ServiceProvider.GetService<ILifecycleCoordinator>();

    // Evict expired entries from the dedup cache (cheap scan each cycle)
    _processedEventCache.EvictExpired();

    // DIAGNOSTIC: Log which service/instance is processing checkpoints
    LogProcessingWorkBatchForService(_logger, _instanceProvider.ServiceName, _instanceProvider.InstanceId);

    // 1-4. Gather pending completions, submit to database, get work batch
    var (workBatch, completionsToSend, failuresToSend) = await _submitCompletionsAndClaimWorkAsync(
      workCoordinator, cancellationToken);

    // Update watch-list from tick results (drain mode stream assignments)
    var newAssignmentCount = _updateWatchListFromTick(workBatch);

    // DIAGNOSTIC: Log perspective work count
    if (workBatch.PerspectiveWork.Count > 0) {
      Console.WriteLine($"[PW-DIAG] [{_instanceProvider.ServiceName}] PerspectiveWorker instanceId={_instanceProvider.InstanceId} got {workBatch.PerspectiveWork.Count} perspective events");
    }

    // 5-8. Reconcile acknowledgements and prepare work groups
    var groupedWork = _reconcileAcknowledgementsAndPrepareWork(workBatch);

    // Record batch composition metrics and tracing tags
    _recordBatchMetrics(batchActivity, workBatch, groupedWork, completionsToSend, failuresToSend);

    // Diagnostic logging for batch composition
    _logBatchComposition(workBatch, groupedWork);

    // Use persistent field — events that fail WhenAll carry over to next tick for re-check
    var batchProcessedEvents = _pendingPostLifecycleEvents;

    // Process perspective work using IPerspectiveRunner (once per stream/perspective group)
    // When drain mode is active (PerspectiveStreamIds populated), skip legacy path —
    // _processDrainModeStreamsAsync handles processing via GetStreamEventsAsync.
    var legacyWork = workBatch.PerspectiveStreamIds.Count > 0 ? [] : groupedWork;
    await Parallel.ForEachAsync(
      legacyWork,
      new ParallelOptions {
        MaxDegreeOfParallelism = _options.MaxConcurrentPerspectives,
        CancellationToken = cancellationToken
      },
      async (group, ct) => {
        var streamId = group.Key.StreamId;
        var perspectiveName = group.Key.PerspectiveName;

        // Each parallel group gets its own DI scope for scoped services (IEventStore, IPerspectiveRunnerRegistry)
        await using var groupScope = _scopeFactory.CreateAsyncScope();
        var groupWorkCoordinator = groupScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
        var groupReceptorInvoker = groupScope.ServiceProvider.GetService<IReceptorInvoker>();
        var groupLifecycleCoordinator = groupScope.ServiceProvider.GetService<ILifecycleCoordinator>();

        // === Phase 1: Resolve dependencies and load events to extract trace context ===
        var (checkpoint, runner, eventStore, upcomingEvents, perspectiveParentContext) =
          await _resolveDependenciesAndLoadEventsAsync(
            groupScope, groupWorkCoordinator, groupReceptorInvoker, streamId, perspectiveName,
            batchActivity, effectiveParent, ct);

        // Skip if runner could not be resolved
        if (runner is null) {
          return;
        }

        var lastProcessedEventId = checkpoint?.LastEventId;

        // === Phase 2: Create perspective activity with proper parent context ===
        var enablePerspectiveSpans = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Perspectives) ?? false;
        using var perspectiveActivity = enablePerspectiveSpans
          ? WhizbangActivitySource.Tracing.StartActivity(
              $"Perspective {perspectiveName}",
              ActivityKind.Internal,
              parentContext: perspectiveParentContext)
          : null;
        _tagPerspectiveActivity(perspectiveActivity, perspectiveName, streamId, upcomingEvents, perspectiveParentContext);

        // Check if Lifecycle tracing is enabled via TraceComponents
        var enableLifecycleSpans = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;

        var streamCtx = new PerspectiveStreamContext(streamId, perspectiveName, lastProcessedEventId, groupScope.ServiceProvider);

        try {
          // Phase 3.1: Invoke PrePerspective lifecycle stages
          await _invokePrePerspectiveLifecycleAsync(
            upcomingEvents, enableLifecycleSpans, groupLifecycleCoordinator, groupReceptorInvoker,
            streamCtx, runner, ct);

          // Phase 3.2: Execute perspective runner (rewind or normal path)
          var (result, processingMode, rewindLockSkipped) = await _executePerspectiveRunnerAsync(
            group, runner, checkpoint, streamCtx,
            enablePerspectiveSpans, ct);

          // Skip this group ONLY if rewind lock could not be acquired.
          // Do NOT skip when Status=None from normal path (no new events) — that's legitimate
          // and downstream lifecycle stages may still need to fire.
          if (rewindLockSkipped) {
            return;
          }

          // Phase 3a: Load events that were just processed
          var processedEvents = await _loadAndLogProcessedEventsAsync(
            groupReceptorInvoker, eventStore, result, streamId, perspectiveName,
            lastProcessedEventId, ct);

          // Collect processed events for PostLifecycle firing at batch end (deduplicate by event ID)
          foreach (var envelope in processedEvents) {
            batchProcessedEvents.TryAdd(envelope.MessageId.Value, (envelope, streamId));
          }

          // Phase 3c: Report completion and sync signals
          await _reportCompletionAndSignalSyncAsync(
            result, processedEvents, groupWorkCoordinator, streamId, perspectiveName, ct);

          // Phase 3d: PostPerspective lifecycle (per-perspective)
          await _invokePostPerspectiveLifecycleAsync(
            processedEvents, groupReceptorInvoker, enableLifecycleSpans, streamCtx,
            result, processingMode, ct);

          LogPerspectiveCursorCompleted(_logger, perspectiveName, streamId, result.LastEventId);

          // Buffer event completions and update dedup cache
          _bufferCompletionsAndUpdateCache(group, processedEvents, groupLifecycleCoordinator, perspectiveName);

          // Fire processing hook after confirmed successful perspective processing
          if (processedEvents.Count > 0) {
            OnPerspectiveEventProcessed?.Invoke(new PerspectiveEventProcessedEvent {
              PerspectiveName = perspectiveName,
              StreamId = streamId,
              EventCount = processedEvents.Count
            });
          }

          // Record per-stream metrics
          _metrics?.StreamsUpdated.Add(1);
          if (processedEvents.Count > 0) {
            _metrics?.EventsProcessed.Add(processedEvents.Count);
          }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogErrorProcessingPerspectiveCursor(_logger, ex, perspectiveName, streamId);
          _metrics?.Errors.Add(1);

          // Signal sync tracker so awaiters don't hang indefinitely waiting for
          // events that will never be processed by this perspective.
          if (_syncEventTracker is not null && upcomingEvents is { Count: > 0 }) {
            var failedEventIds = upcomingEvents.Select(e => e.MessageId.Value).ToList();
            _syncEventTracker.MarkProcessedByPerspective(failedEventIds, perspectiveName);
          }

          var failure = new PerspectiveCursorFailure {
            StreamId = streamId,
            PerspectiveName = perspectiveName,
            LastEventId = Guid.Empty, // We don't know which event failed
            Status = PerspectiveProcessingStatus.Failed,
            Error = ex.Message
          };

          // Report failure via strategy
          await _completionStrategy.ReportFailureAsync(failure, groupWorkCoordinator, ct);
          throw; // Never swallow exceptions
        }
      });

    // === Drain mode: process watched streams via batch-fetch ===
    await _processDrainModeStreamsAsync(
      workCoordinator, batchProcessedEvents, batchActivity, effectiveParent, cancellationToken);

    // Phase 5: Fire PostLifecycle once per unique event — ONLY after ALL perspectives complete (WhenAll)
    await _firePostLifecycleDetached(
      batchProcessedEvents, lifecycleCoordinator, receptorInvoker, groupedWork,
      scope.ServiceProvider, cancellationToken);

    // Log summary and record batch-level metrics
    _logBatchSummary(completionsToSend, failuresToSend, workBatch);
    _metrics?.BatchesProcessed.Add(1);
    _metrics?.BatchDuration.Record(batchSw.Elapsed.TotalMilliseconds);

    // Track work state transitions for OnWorkProcessingStarted / OnWorkProcessingIdle callbacks
    var hasWork = workBatch.PerspectiveWork.Count > 0 || newAssignmentCount > 0;
    _updateWorkStateTracking(hasWork);

    return newAssignmentCount;
  }

  /// <summary>
  /// Gathers pending completions/failures, drains buffered event completions,
  /// builds the ProcessWorkBatchRequest, and submits it to the work coordinator.
  /// Returns the work batch along with the completions/failures that were sent.
  /// </summary>
  private async Task<(WorkBatch WorkBatch, PerspectiveCursorCompletion[] CompletionsSent, PerspectiveCursorFailure[] FailuresSent)>
    _submitCompletionsAndClaimWorkAsync(IWorkCoordinator workCoordinator, CancellationToken cancellationToken) {

    // 1. Get pending items (status = Pending, not yet sent)
    var pendingCompletions = _completionStrategy.GetPendingCompletions();
    var pendingFailures = _completionStrategy.GetPendingFailures();

    // 2. Extract actual completion data for ProcessWorkBatchAsync
    var completionsToSend = pendingCompletions.Select(tc => tc.Completion).ToArray();
    var failuresToSend = pendingFailures.Select(tc => tc.Completion).ToArray();

    // 2b. Drain buffered perspective event completions (WorkIds for wh_perspective_events deletion)
    var eventCompletionsList = new List<PerspectiveEventCompletion>();
    while (_pendingEventCompletions.TryDequeue(out var ec)) {
      eventCompletionsList.Add(ec);
    }
    var eventCompletionsToSend = eventCompletionsList.ToArray();

    // 3. Mark as Sent BEFORE calling ProcessWorkBatchAsync
    var sentAt = DateTimeOffset.UtcNow;
    _completionStrategy.MarkAsSent(pendingCompletions, pendingFailures, sentAt);

    // 4. Call ProcessWorkBatchAsync (may throw or return partial acknowledgement)
    try {
      var request = new ProcessWorkBatchRequest {
        InstanceId = _instanceProvider.InstanceId,
        ServiceName = _instanceProvider.ServiceName,
        HostName = _instanceProvider.HostName,
        ProcessId = _instanceProvider.ProcessId,
        Metadata = _options.InstanceMetadata,
        OutboxCompletions = [],
        OutboxFailures = [],
        InboxCompletions = [],
        InboxFailures = [],
        ReceptorCompletions = [],
        ReceptorFailures = [],
        PerspectiveCompletions = completionsToSend,
        PerspectiveEventCompletions = eventCompletionsToSend,
        PerspectiveFailures = failuresToSend,
        NewOutboxMessages = [],
        NewInboxMessages = [],
        RenewOutboxLeaseIds = [],
        RenewInboxLeaseIds = [],
        Flags = _options.DebugMode ? WorkBatchOptions.DebugMode : WorkBatchOptions.None,
        PartitionCount = _options.PartitionCount,
        LeaseSeconds = _options.LeaseSeconds,
        StaleThresholdSeconds = _options.StaleThresholdSeconds,
        MaxPerspectiveStreams = _options.MaxPerspectiveStreams
      };
      var claimSw = System.Diagnostics.Stopwatch.StartNew();
      var workBatch = await workCoordinator.ProcessWorkBatchAsync(request, cancellationToken);
      _metrics?.ClaimDuration.Record(claimSw.Elapsed.TotalMilliseconds);
      return (workBatch, completionsToSend, failuresToSend);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Database failure: Completions remain in 'Sent' status
      // ResetStale() will move them back to 'Pending' after timeout
      LogErrorProcessingWorkBatch(_logger, ex);
      _metrics?.Errors.Add(1);
      throw; // Never swallow exceptions
    }
  }

  /// <summary>
  /// Updates the watch-list from the tick's PerspectiveStreamIds.
  /// New streams are added with cooldown = 0 (active).
  /// Existing streams already in the watch-list have their cooldown reset to 0.
  /// Returns the count of new stream assignments from this tick.
  /// </summary>
  private int _updateWatchListFromTick(WorkBatch workBatch) {
    var streamIds = workBatch.PerspectiveStreamIds;
    if (streamIds.Count == 0) {
      return 0;
    }

    foreach (var streamId in streamIds) {
      // New or existing — set cooldown to 0 (active)
      _watchList[streamId] = 0;
    }

    return streamIds.Count;
  }

  /// <summary>
  /// Manages watch-list cooldown after a processing cycle.
  /// Streams that had events stay active (cooldown = 0).
  /// Streams with no events get cooldown set, then decremented each cycle.
  /// Streams whose cooldown reaches 0 after decrement are removed.
  /// </summary>
  private void _manageWatchListCooldowns(HashSet<Guid> streamsWithEvents) {
    var toRemove = new List<Guid>();
    foreach (var (streamId, cooldown) in _watchList) {
      if (streamsWithEvents.Contains(streamId)) {
        // Had events — keep active
        _watchList[streamId] = 0;
        continue;
      }

      if (cooldown == 0) {
        // First cycle with no events — start cooldown
        _watchList[streamId] = _options.WatchListCooldownCycles;
      } else {
        var newCooldown = cooldown - 1;
        if (newCooldown <= 0) {
          toRemove.Add(streamId);
        } else {
          _watchList[streamId] = newCooldown;
        }
      }
    }

    foreach (var streamId in toRemove) {
      _watchList.Remove(streamId);
      _cursorCache.InvalidateStream(streamId);
    }
  }

  /// <summary>
  /// Drain mode stream processing: batch-fetches events for a capped set of watched streams,
  /// parallelizes across (stream, perspective) pairs, and batches completions into a single call.
  /// </summary>
  private async Task _processDrainModeStreamsAsync(
      IWorkCoordinator workCoordinator,
      ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> batchProcessedEvents,
      Activity? batchActivity,
      ActivityContext effectiveParent,
      CancellationToken cancellationToken) {

    if (_watchList.Count == 0) {
      return;
    }

    // Lazy-resolve IEventTypeProvider from scoped provider if constructor injection missed it.
    // This is a singleton, so resolving once and caching is safe.
    if (_eventTypeProvider is null) {
      await using var resolveScope = _scopeFactory.CreateAsyncScope();
      _eventTypeProvider = resolveScope.ServiceProvider.GetService<IEventTypeProvider>();
    }

    // Optimization #2: Cap streams per cycle to keep cycles fast and responsive
    var watchedStreamIds = _watchList.Keys
      .Take(_options.MaxConcurrentPerspectives)
      .ToArray();

    // Batch-fetch events for capped set of watched streams
    var streamEvents = await workCoordinator.GetStreamEventsAsync(
      _instanceProvider.InstanceId, watchedStreamIds, cancellationToken);

    // Group by StreamId, deduplicate events (same event_id appears per perspective in wh_perspective_events)
    var eventsByStream = streamEvents.GroupBy(e => e.StreamId).ToList();
    var streamsWithEvents = new HashSet<Guid>(eventsByStream.Select(g => g.Key));

    // Build (stream, perspective) work items for parallel processing
    // Raw events are shared per stream; deserialization happens once per stream inside the parallel loop
    var deserializedCache = new ConcurrentDictionary<Guid, IReadOnlyList<MessageEnvelope<IEvent>>>();
    var workItems = new List<(Guid StreamId, string PerspectiveName, List<StreamEventData> RawEvents)>();
    foreach (var streamGroup in eventsByStream) {
      var streamId = streamGroup.Key;
      var rawEvents = streamGroup.ToList();

      // Determine applicable perspectives from event types
      var perspectiveNames = new HashSet<string>();
      if (_perspectivesPerEventType is not null) {
        foreach (var evt in rawEvents) {
          if (_perspectivesPerEventType.TryGetValue(evt.EventType, out var names)) {
            foreach (var name in names) {
              perspectiveNames.Add(name);
            }
          }
        }
      }

      foreach (var perspectiveName in perspectiveNames) {
        workItems.Add((streamId, perspectiveName, rawEvents));
      }
    }

    // Optimization #3: Collect all work_item_ids for a single batched completion call
    var allCompletedWorkIds = new ConcurrentBag<Guid>();

    // Process all (stream, perspective) pairs in parallel
    await Parallel.ForEachAsync(
      workItems,
      new ParallelOptions {
        MaxDegreeOfParallelism = _options.MaxConcurrentPerspectives,
        CancellationToken = cancellationToken
      },
      async (workItem, ct) => {
        var (streamId, perspectiveName, rawEvents) = workItem;

        await using var groupScope = _scopeFactory.CreateAsyncScope();
        var groupWorkCoordinator = groupScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
        var groupReceptorInvoker = groupScope.ServiceProvider.GetService<IReceptorInvoker>();
        var groupLifecycleCoordinator = groupScope.ServiceProvider.GetService<ILifecycleCoordinator>();
        var registry = groupScope.ServiceProvider.GetService<IPerspectiveRunnerRegistry>();
        var eventStore = groupScope.ServiceProvider.GetRequiredService<IEventStore>();

        if (registry is null) {
          return;
        }

        var runner = registry.GetRunner(perspectiveName, groupScope.ServiceProvider);
        if (runner is null) {
          return;
        }

        // Deserialize events ONCE per stream, cache for reuse across perspectives.
        // Resolve IEventTypeProvider from scoped provider (more reliable than constructor-injected field).
        var typedEvents = deserializedCache.GetOrAdd(streamId, _ => {
          var scopedEventTypeProvider = groupScope.ServiceProvider.GetService<IEventTypeProvider>();
          var eventTypes = scopedEventTypeProvider?.GetEventTypes() ?? [];
          return eventStore.DeserializeStreamEvents(rawEvents, eventTypes);
        });

        // If deserialization produced no typed events, fall back to RunAsync (reads from DB directly).
        // This handles the case where IEventTypeProvider or JsonSerializerOptions can't resolve the types.
        var useRunWithEvents = typedEvents.Count > 0;

        try {
          // Get cursor position — try cache first (drain mode optimization), fall back to DB.
          Guid? lastProcessedEventId;
          if (!_cursorCache.TryGet(streamId, perspectiveName, out lastProcessedEventId)) {
            var checkpoint = await groupWorkCoordinator.GetPerspectiveCursorAsync(
              streamId, perspectiveName, ct);
            lastProcessedEventId = checkpoint?.LastEventId;
          }

          // RunWithEventsAsync (pre-supplied events) or RunAsync (reads from DB) depending on deserialization success.
          var result = useRunWithEvents
            ? await runner.RunWithEventsAsync(streamId, perspectiveName, lastProcessedEventId, typedEvents, ct)
            : await runner.RunAsync(streamId, perspectiveName, lastProcessedEventId, ct);

          // Update cursor cache for next cycle
          if (result.Status == PerspectiveProcessingStatus.Completed && result.LastEventId != Guid.Empty) {
            _cursorCache.Set(streamId, perspectiveName, result.LastEventId);
          }

          // Load processed events for PostAllPerspectives and sync signaling.
          // When RunAsync fallback is used (typedEvents empty), load from event store.
          var processedEvents = typedEvents.Count > 0
            ? typedEvents.ToList()
            : await _loadAndLogProcessedEventsAsync(
                groupReceptorInvoker, eventStore, result, streamId, perspectiveName,
                lastProcessedEventId, ct);

          // Report completion and sync signals
          await _reportCompletionAndSignalSyncAsync(
            result, processedEvents, groupWorkCoordinator, streamId, perspectiveName, ct);

          // Collect processed events for PostAllPerspectives firing (WhenAll gate)
          foreach (var envelope in processedEvents) {
            batchProcessedEvents.TryAdd(envelope.MessageId.Value, (envelope, streamId));
          }

          // Signal perspective complete for WhenAll tracking
          if (groupLifecycleCoordinator is not null) {
            foreach (var envelope in processedEvents) {
              groupLifecycleCoordinator.SignalPerspectiveComplete(envelope.MessageId.Value, perspectiveName);
            }
          }

          LogPerspectiveCursorCompleted(_logger, perspectiveName, streamId, result.LastEventId);

          // Fire processing hook
          if (result.EventsProcessed > 0) {
            OnPerspectiveEventProcessed?.Invoke(new PerspectiveEventProcessedEvent {
              PerspectiveName = perspectiveName,
              StreamId = streamId,
              EventCount = result.EventsProcessed
            });
          }

          // Record metrics
          _metrics?.StreamsUpdated.Add(1);
          if (result.EventsProcessed > 0) {
            _metrics?.EventsProcessed.Add(result.EventsProcessed);
          }

          // Collect work_item_ids for batched completion
          foreach (var evt in rawEvents) {
            allCompletedWorkIds.Add(evt.EventWorkId);
          }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogErrorProcessingPerspectiveCursor(_logger, ex, perspectiveName, streamId);
          _metrics?.Errors.Add(1);
          throw;
        }
      });

    // Optimization #3: Single batched completion call instead of per-stream
    var workIdsToComplete = allCompletedWorkIds.Distinct().ToArray();
    System.IO.File.AppendAllText("/tmp/drain_completion.txt",
      $"[{DateTime.UtcNow:HH:mm:ss}] workItems={workItems.Count}, allCompletedWorkIds={allCompletedWorkIds.Count}, distinct={workIdsToComplete.Length}\n");
    if (workIdsToComplete.Length > 0) {
      await workCoordinator.CompletePerspectiveEventsAsync(workIdsToComplete, cancellationToken);
    }

    // Manage watch-list cooldowns (optimization #6: immediate removal on 0 events)
    _manageWatchListCooldowns(streamsWithEvents);
  }

  /// <summary>
  /// Loads upcoming events for a stream from the event store, starting after the last processed event.
  /// Used by drain mode to get MessageEnvelope events for lifecycle stages.
  /// </summary>
  private async Task<List<MessageEnvelope<IEvent>>> _loadUpcomingEventsForStreamAsync(
      IEventStore eventStore,
      Guid streamId,
      Guid? lastProcessedEventId,
      CancellationToken ct) {
    if (_eventTypeProvider is null) {
      return [];
    }

    var eventTypes = _eventTypeProvider.GetEventTypes();
    if (eventTypes.Count == 0) {
      return [];
    }

    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      lastProcessedEventId,
      Guid.Empty, // Read all events after lastProcessedEventId
      eventTypes,
      ct
    );

    return events;
  }

  /// <summary>
  /// Extracts acknowledgement counts from the work batch metadata,
  /// reconciles completion state, and groups work items for processing.
  /// </summary>
  private List<IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork>>
    _reconcileAcknowledgementsAndPrepareWork(WorkBatch workBatch) {

    // 5. Extract acknowledgement counts from workBatch metadata (from first row)
    var (completionsProcessed, failuresProcessed) = _extractAcknowledgementCounts(workBatch);

    // 6. Mark as Acknowledged based on counts from SQL
    _completionStrategy.MarkAsAcknowledged(completionsProcessed, failuresProcessed);

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
  /// Extracts completion/failure acknowledgement counts from the first metadata-bearing row
  /// across perspective, outbox, or inbox work results.
  /// </summary>
  private static (int CompletionsProcessed, int FailuresProcessed) _extractAcknowledgementCounts(WorkBatch workBatch) {
    var completionsProcessed = 0;
    var failuresProcessed = 0;

    // Try perspective work, then outbox, then inbox for metadata
    var metadataRow = workBatch.PerspectiveWork.FirstOrDefault()?.Metadata
      ?? workBatch.OutboxWork.FirstOrDefault()?.Metadata
      ?? workBatch.InboxWork.FirstOrDefault()?.Metadata;

    if (metadataRow != null) {
      if (metadataRow.TryGetValue("perspective_completions_processed", out var compCount)) {
        completionsProcessed = compCount.GetInt32();
      }
      if (metadataRow.TryGetValue("perspective_failures_processed", out var failCount)) {
        failuresProcessed = failCount.GetInt32();
      }
    }

    return (completionsProcessed, failuresProcessed);
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
        new KeyValuePair<string, object?>("perspective_name", streamCtx.PerspectiveName));

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

          // Invalidate cursor cache after rewind — cursor position has changed
          _cursorCache.Invalidate(streamId, perspectiveName);
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
          new KeyValuePair<string, object?>("perspective_name", perspectiveName),
          new KeyValuePair<string, object?>("has_snapshot", hasSnapshot));
        _metrics?.RewindDuration.Record(rewindDurationMs,
          new KeyValuePair<string, object?>("perspective_name", perspectiveName));
        _metrics?.RewindEventsReplayed.Record(result.EventsProcessed,
          new KeyValuePair<string, object?>("perspective_name", perspectiveName));

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
    // When processedEvents is empty (drain mode), fall back to result.ProcessedEventIds from the runner.
    var syncEventIds = processedEvents.Count > 0
      ? processedEvents.Select(e => e.MessageId.Value).ToList()
      : result.ProcessedEventIds?.ToList() ?? [];
    if (syncEventIds.Count > 0 && _syncEventTracker is not null) {
      var processedEventIds = syncEventIds;
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
        syncEventIds.Count, _syncEventTracker is not null);
#pragma warning restore CA1848
    }

    // Phase 3c.1: Signal checkpoint updated for perspective sync
    // This notifies any waiting sync awaiters that the perspective has processed up to this event
    if (result.PerspectiveType is not null) {
      _syncSignaler?.SignalCheckpointUpdated(result.PerspectiveType, streamId, result.LastEventId);
    }
  }

  /// <summary>
  /// Phase 3d: Invokes PostPerspective lifecycle receptors and processes tags.
  /// PostPerspective fires PER PERSPECTIVE via direct invoker (not coordinator).
  /// </summary>
  private async Task _invokePostPerspectiveLifecycleAsync(
      List<MessageEnvelope<IEvent>> processedEvents,
      IReceptorInvoker? receptorInvoker,
      bool enableLifecycleSpans,
      PerspectiveStreamContext streamCtx,
      PerspectiveCursorCompletion result,
      ProcessingMode? processingMode,
      CancellationToken cancellationToken) {

    LogCheckingPostPerspectiveInline(_logger, processedEvents.Count, receptorInvoker is not null);

    using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostPerspective", ActivityKind.Internal) : null) {
      if (processedEvents.Count > 0 && receptorInvoker is not null) {
        LogInvokingPostPerspectiveInline(_logger, processedEvents.Count, streamCtx.PerspectiveName, streamCtx.StreamId);

        await _invokeLifecycleReceptorsForEventsAsync(
          processedEvents, streamCtx, result.PerspectiveType, result.LastEventId,
          LifecycleStage.PostPerspectiveInline, cancellationToken, processingMode);
        await _invokeLifecycleReceptorsForEventsAsync(
          processedEvents, streamCtx, result.PerspectiveType, result.LastEventId,
          LifecycleStage.ImmediateDetached, cancellationToken, processingMode);
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
      CancellationToken cancellationToken) {

    if (batchProcessedEvents.IsEmpty) {
      return;
    }

    if (lifecycleCoordinator is not null) {
      await _firePostLifecycleWithCoordinatorAsync(
        batchProcessedEvents, lifecycleCoordinator, groupedWork, scopedProvider, cancellationToken);
    } else if (receptorInvoker is not null) {
      await _firePostLifecycleFallbackAsync(
        batchProcessedEvents, receptorInvoker, scopedProvider, cancellationToken, _detachedTasks.Add);
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

    // Replay signals — perspectives already completed during the group loop (or drain mode),
    // but expectations may have been registered just above. Replaying ensures WhenAll resolves.
    if (groupedWork.Count > 0) {
      // Legacy path: replay from grouped work
      foreach (var group in groupedWork) {
        var gPerspectiveName = group.Key.PerspectiveName;
        foreach (var (eventId, _) in batchProcessedEvents.Where(e => e.Value.StreamId == group.Key.StreamId)) {
          lifecycleCoordinator.SignalPerspectiveComplete(eventId, gPerspectiveName);
        }
      }
    } else if (_perspectivesPerEventType is not null) {
      // Drain mode: replay using registry — all perspectives that handle each event type
      foreach (var (eventId, (envelope, _)) in batchProcessedEvents) {
        var eventType = envelope.Payload.GetType();
        var eventTypeKey = $"{eventType.FullName}, {eventType.Assembly.GetName().Name}";
        if (_perspectivesPerEventType.TryGetValue(eventTypeKey, out var perspectives)) {
          foreach (var perspectiveName in perspectives) {
            lifecycleCoordinator.SignalPerspectiveComplete(eventId, perspectiveName);
          }
        }
      }
    }

    foreach (var (eventId, (envelope, _)) in batchProcessedEvents) {
      // WhenAll gate: PostAllPerspectives fires only when all perspectives signaled complete
      if (!lifecycleCoordinator.AreAllPerspectivesComplete(eventId)) {
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

      // PostLifecycle fired — remove from persistent tracking to prevent unbounded growth.
      // The tracking instance's stage guard prevents re-firing in subsequent cycles.
      batchProcessedEvents.TryRemove(eventId, out _);
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
      Action<Task>? trackDetachedTask = null) {

    foreach (var (_, (envelope, streamId)) in batchProcessedEvents) {
      var context = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PostLifecycleDetached,
        StreamId = streamId,
        PerspectiveType = null,
        MessageSource = MessageSource.Local,
        AttemptNumber = 1
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
  /// Invokes lifecycle receptors for the given events at the specified stage.
  /// Used for both PostPerspectiveDetached (before checkpoint save) and PostPerspectiveInline (after checkpoint save).
  /// </summary>
  private async Task _invokeLifecycleReceptorsForEventsAsync(
      List<MessageEnvelope<IEvent>> processedEvents,
      PerspectiveStreamContext streamCtx,
      Type? perspectiveType,
      Guid currentEventId,
      LifecycleStage stage,
      CancellationToken cancellationToken,
      ProcessingMode? processingMode = null) {

    var scopedReceptorInvoker = streamCtx.ScopedProvider.GetService<IReceptorInvoker>()
      ?? throw new InvalidOperationException(
        "IReceptorInvoker is required for lifecycle stage invocation but was not registered. " +
        "Ensure AddWhizbangReceptorInvoker() is called during DI setup.");

    try {
      // Create lifecycle context with stream and perspective information
      var context = new LifecycleExecutionContext {
        CurrentStage = stage,
        StreamId = streamCtx.StreamId,
        PerspectiveType = perspectiveType,
        LastProcessedEventId = currentEventId,
        MessageSource = MessageSource.Local,
        AttemptNumber = 1, // Perspectives process from local event store
        ProcessingMode = processingMode
      };

      // Invoke receptors for each event
      foreach (var envelope in processedEvents) {
        // Establish security context BEFORE invoking lifecycle receptors
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
    EventId = 4,
    Level = LogLevel.Warning,
    Message = "Database not ready on startup - skipping initial perspective cursor processing"
  )]
  static partial void LogDatabaseNotReadyOnStartup(ILogger logger);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Error,
    Message = "Error processing initial perspective cursors on startup"
  )]
  static partial void LogErrorProcessingInitialCheckpoints(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Information,
    Message = "Database not ready, skipping perspective cursor processing (consecutive checks: {ConsecutiveCount})"
  )]
  static partial void LogDatabaseNotReady(ILogger logger, int consecutiveCount);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Warning,
    Message = "Database not ready for {ConsecutiveCount} consecutive polling cycles. Perspective worker is paused."
  )]
  static partial void LogDatabaseNotReadyWarning(ILogger logger, int consecutiveCount);

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
  /// Stale instance threshold in seconds.
  /// Instances that haven't sent a heartbeat for this duration will be removed.
  /// Default: 600 (10 minutes)
  /// </summary>
  public int StaleThresholdSeconds { get; set; } = 600;

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
  /// Maximum number of streams to process concurrently within a single batch.
  /// Higher values improve throughput when multiple streams have pending work.
  /// Different streams are independent and can safely run in parallel.
  /// Default: 30
  /// </summary>
  public int MaxConcurrentPerspectives { get; set; } = 30;

  /// <summary>
  /// Maximum number of streams to claim per tick for perspective processing (drain mode).
  /// NULL uses the default from wh_settings. Passed to process_work_batch as p_max_perspective_streams.
  /// Default: 500
  /// </summary>
  public int? MaxPerspectiveStreams { get; set; } = 500;

  /// <summary>
  /// Number of empty processing cycles a stream stays on the watch-list before removal.
  /// Handles the race where new messages arrive just after the worker checked.
  /// Default: 1 (aggressive removal to keep batch fetches lean)
  /// </summary>
  public int WatchListCooldownCycles { get; set; } = 1;

  /// <summary>
  /// Retry configuration for completion acknowledgement.
  /// Controls exponential backoff when ProcessWorkBatchAsync fails.
  /// </summary>
  public WorkerRetryOptions RetryOptions { get; set; } = new();
}
