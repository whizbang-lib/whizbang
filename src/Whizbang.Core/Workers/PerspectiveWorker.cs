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
  LifecycleCoordinatorMetrics? coordinatorMetrics = null
) : BackgroundService {
#pragma warning restore S107
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IDatabaseReadinessCheck _databaseReadinessCheck = databaseReadinessCheck ?? new DefaultDatabaseReadinessCheck();
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions = tracingOptions;
  private readonly IEventTypeProvider? _eventTypeProvider = eventTypeProvider;
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
  private readonly IPerspectiveStreamLocker? _streamLocker = streamLocker;
  private readonly PerspectiveStreamLockOptions _streamLockOptions = streamLockOptions?.Value ?? new PerspectiveStreamLockOptions();

  // Perspective event completions (WorkIds to delete from wh_perspective_events)
  private readonly System.Collections.Concurrent.ConcurrentQueue<PerspectiveEventCompletion> _pendingEventCompletions = new();

  // Cache of streams that have been bootstrapped this session (skip re-check)
  private readonly HashSet<(Guid StreamId, string PerspectiveName)> _bootstrappedThisSession = [];

  // Two-phase TTL cache to prevent duplicate Apply when SQL re-delivers events during batched completion window
  private readonly ProcessedEventCache _processedEventCache = new(
    TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.LeaseSeconds),
    timeProvider: timeProvider,
    observer: processedEventCacheObserver
  );

  // Registry-based map: event type (CLR format) → all perspective CLR names that handle it.
  // Built once at startup from IPerspectiveRunnerRegistry. Used to register complete WhenAll
  // expectations per event so PostAllPerspectivesAsync fires once after ALL perspectives complete,
  // not once per batch cycle.
  private Dictionary<string, IReadOnlyList<string>>? _perspectivesPerEventType;

  // Metrics tracking
  private int _consecutiveDatabaseNotReadyChecks;
  private int _consecutiveEmptyPolls;
  private bool _isIdle = true;  // Start in idle state
  private int _batchCycleCount;

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
  /// Groups per-stream perspective processing parameters that travel together through lifecycle phases.
  /// </summary>
  private readonly record struct PerspectiveStreamContext(
    Guid StreamId,
    string PerspectiveName,
    Guid? LastProcessedEventId,
    IServiceProvider ScopedProvider);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogWorkerStarting(_logger, _instanceProvider.InstanceId, _instanceProvider.ServiceName, _instanceProvider.HostName, _instanceProvider.ProcessId, _options.PollingIntervalMilliseconds);

    await _initializePerspectiveRegistryAsync();
    await _processInitialCheckpointsAsync(stoppingToken);

    while (!stoppingToken.IsCancellationRequested) {
      try {
        if (!await _checkDatabaseReadinessAsync(stoppingToken)) {
          await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
          continue;
        }

        await _processWorkBatchAsync(stoppingToken);
        _periodicStaleTrackingCleanup();
      } catch (ObjectDisposedException) {
        break;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogErrorProcessingCheckpoints(_logger, ex);
        throw; // Never swallow exceptions
      }

      try {
        await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
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

  // S3776: Core perspective processing pipeline — inherent complexity from 5-phase lifecycle (claim/apply/buffer/postPerspective/postLifecycle)
#pragma warning disable S3776
  private async Task _processWorkBatchAsync(CancellationToken cancellationToken) {
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

    // 5-8. Reconcile acknowledgements and prepare work groups
    var groupedWork = _reconcileAcknowledgementsAndPrepareWork(workBatch);

    // Record batch composition metrics and tracing tags
    _recordBatchMetrics(batchActivity, workBatch, groupedWork, completionsToSend, failuresToSend);

    // Diagnostic logging for batch composition
    _logBatchComposition(workBatch, groupedWork);

    // Collect all processed events across groups for PostLifecycle firing
    var batchProcessedEvents = new Dictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();

    // Process perspective work using IPerspectiveRunner (once per stream/perspective group)
    foreach (var group in groupedWork) {
      var streamId = group.Key.StreamId;
      var perspectiveName = group.Key.PerspectiveName;

      // === Phase 1: Resolve dependencies and load events to extract trace context ===
      var (checkpoint, runner, eventStore, upcomingEvents, perspectiveParentContext) =
        await _resolveDependenciesAndLoadEventsAsync(
          scope, workCoordinator, receptorInvoker, streamId, perspectiveName,
          batchActivity, effectiveParent, cancellationToken);

      // Skip if runner could not be resolved
      if (runner is null) {
        continue;
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

      var streamCtx = new PerspectiveStreamContext(streamId, perspectiveName, lastProcessedEventId, scope.ServiceProvider);

      try {
        // Phase 3.1: Invoke PrePerspective lifecycle stages
        await _invokePrePerspectiveLifecycleAsync(
          upcomingEvents, enableLifecycleSpans, lifecycleCoordinator, receptorInvoker,
          streamCtx, cancellationToken);

        // Phase 3.2: Execute perspective runner (rewind or normal path)
        var (result, processingMode) = await _executePerspectiveRunnerAsync(
          group, runner, checkpoint, streamCtx,
          enablePerspectiveSpans, cancellationToken);

        // Skip this group if rewind lock could not be acquired (sentinel: Status=None)
        if (result.Status == PerspectiveProcessingStatus.None) {
          continue;
        }

        // Phase 3a: Load events that were just processed
        var processedEvents = await _loadAndLogProcessedEventsAsync(
          receptorInvoker, eventStore, result, streamId, perspectiveName,
          lastProcessedEventId, cancellationToken);

        // Collect processed events for PostLifecycle firing at batch end (deduplicate by event ID)
        foreach (var envelope in processedEvents) {
          batchProcessedEvents.TryAdd(envelope.MessageId.Value, (envelope, streamId));
        }

        // Phase 3c: Report completion and sync signals
        await _reportCompletionAndSignalSyncAsync(
          result, processedEvents, workCoordinator, streamId, perspectiveName, cancellationToken);

        // Phase 3d: PostPerspective lifecycle (per-perspective)
        await _invokePostPerspectiveLifecycleAsync(
          processedEvents, receptorInvoker, enableLifecycleSpans, streamCtx,
          result, processingMode, cancellationToken);

        LogPerspectiveCursorCompleted(_logger, perspectiveName, streamId, result.LastEventId);

        // Buffer event completions and update dedup cache
        _bufferCompletionsAndUpdateCache(group, processedEvents, lifecycleCoordinator, perspectiveName);

        // Record per-stream metrics
        _metrics?.StreamsUpdated.Add(1);
        if (processedEvents.Count > 0) {
          _metrics?.EventsProcessed.Add(processedEvents.Count);
        }
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogErrorProcessingPerspectiveCursor(_logger, ex, perspectiveName, streamId);
        _metrics?.Errors.Add(1);

        var failure = new PerspectiveCursorFailure {
          StreamId = streamId,
          PerspectiveName = perspectiveName,
          LastEventId = Guid.Empty, // We don't know which event failed
          Status = PerspectiveProcessingStatus.Failed,
          Error = ex.Message
        };

        // Report failure via strategy
        await _completionStrategy.ReportFailureAsync(failure, workCoordinator, cancellationToken);
        throw; // Never swallow exceptions
      }
    }

    // Phase 5: Fire PostLifecycle once per unique event — ONLY after ALL perspectives complete (WhenAll)
    await _firePostLifecycleAsync(
      batchProcessedEvents, lifecycleCoordinator, receptorInvoker, groupedWork,
      scope.ServiceProvider, cancellationToken);

    // Log summary and record batch-level metrics
    _logBatchSummary(completionsToSend, failuresToSend, workBatch);
    _metrics?.BatchesProcessed.Add(1);
    _metrics?.BatchDuration.Record(batchSw.Elapsed.TotalMilliseconds);

    // Track work state transitions for OnWorkProcessingStarted / OnWorkProcessingIdle callbacks
    _updateWorkStateTracking(workBatch.PerspectiveWork.Count > 0);
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
        StaleThresholdSeconds = _options.StaleThresholdSeconds
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
      CancellationToken cancellationToken) {

    using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PrePerspective", ActivityKind.Internal) : null) {
      if (upcomingEvents is { Count: > 0 }) {
        try {
          foreach (var envelope in upcomingEvents) {
            await _establishSecurityContextAsync(envelope, streamCtx.ScopedProvider, cancellationToken);

            if (lifecycleCoordinator is not null) {
              // Coordinator path: BeginTracking + AdvanceToAsync (stage guard = exactly-once)
              var tracking = lifecycleCoordinator.BeginTracking(
                envelope.MessageId.Value, envelope, LifecycleStage.PrePerspectiveAsync,
                MessageSource.Local, streamCtx.StreamId);

              // Stage guard ensures these fire once per event, not once per perspective group
              await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveAsync, streamCtx.ScopedProvider, cancellationToken);
              await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveInline, streamCtx.ScopedProvider, cancellationToken);
            } else if (receptorInvoker is not null) {
              // Fallback: direct invocation when coordinator not registered
              var context = new LifecycleExecutionContext {
                CurrentStage = LifecycleStage.PrePerspectiveAsync,
                StreamId = streamCtx.StreamId,
                LastProcessedEventId = streamCtx.LastProcessedEventId,
                MessageSource = MessageSource.Local,
                AttemptNumber = 1
              };
              await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PrePerspectiveAsync, context, cancellationToken);
              await receptorInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync,
                context with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);
              await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PrePerspectiveInline,
                context with { CurrentStage = LifecycleStage.PrePerspectiveInline }, cancellationToken);
              await receptorInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync,
                context with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);
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
  /// The rewind path returns a special continue-sentinel (status=None) when lock acquisition fails.
  /// </summary>
  private async Task<(PerspectiveCursorCompletion Result, ProcessingMode? Mode)>
    _executePerspectiveRunnerAsync(
      IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork> group,
      IPerspectiveRunner runner,
      PerspectiveCursorInfo? checkpoint,
      PerspectiveStreamContext streamCtx,
      bool enablePerspectiveSpans,
      CancellationToken cancellationToken) {

    // Check if any work item in the group has RewindRequired flag
    var groupStatus = group.Aggregate(PerspectiveProcessingStatus.None, (acc, w) => acc | w.Status);
    var needsRewind = groupStatus.HasFlag(PerspectiveProcessingStatus.RewindRequired);
    var rewindTriggerEventId = checkpoint?.RewindTriggerEventId;

    if (needsRewind && rewindTriggerEventId.HasValue) {
      var result = await _executeRewindPathAsync(
        runner, streamCtx.StreamId, streamCtx.PerspectiveName, rewindTriggerEventId.Value,
        enablePerspectiveSpans, cancellationToken);
      return (result, ProcessingMode.Replay);
    }

    // Bootstrap snapshot if needed (existing stream with events but no snapshots)
    await _bootstrapSnapshotIfNeededAsync(runner, streamCtx.StreamId, streamCtx.PerspectiveName, streamCtx.LastProcessedEventId, cancellationToken);

    // Normal path
    var normalResult = await _executeNormalPathAsync(
      runner, streamCtx.StreamId, streamCtx.PerspectiveName, streamCtx.LastProcessedEventId,
      enablePerspectiveSpans, cancellationToken);
    return (normalResult, null);
  }

  /// <summary>
  /// Rewind path: acquire stream lock, restore from snapshot and replay events.
  /// Throws OperationCanceledException if lock cannot be acquired (caller handles via continue).
  /// </summary>
  private async Task<PerspectiveCursorCompletion> _executeRewindPathAsync(
      IPerspectiveRunner runner,
      Guid streamId,
      string perspectiveName,
      Guid rewindTriggerEventId,
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
          // Return sentinel value — caller detects via Status=None and continues to next group
          return new PerspectiveCursorCompletion {
            StreamId = streamId,
            PerspectiveName = perspectiveName,
            LastEventId = Guid.Empty,
            Status = PerspectiveProcessingStatus.None
          };
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
        result = await runner.RewindAndRunAsync(
          streamId, perspectiveName, rewindTriggerEventId, cancellationToken);
        _metrics?.RunnerDuration.Record(runnerSw.Elapsed.TotalMilliseconds);

        activity?.SetTag("whizbang.perspective.status", result.Status.ToString());
        activity?.SetTag("whizbang.perspective.last_event_id", result.LastEventId.ToString());
      }

      // Stop keepalive
      await keepaliveCts.CancelAsync();
      try { await keepaliveTask; } catch (OperationCanceledException) { /* expected */ }
    } finally {
      if (lockAcquired && _streamLocker is not null) {
        await _streamLocker.ReleaseLockAsync(streamId, perspectiveName, _instanceProvider.InstanceId, cancellationToken);
      }
    }

    return result;
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
        || _bootstrappedThisSession.Contains((streamId, perspectiveName))) {
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
      _bootstrappedThisSession.Add((streamId, perspectiveName));
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

    // NOTE: PostPerspectiveAsync is fired from the generated perspective runner, not here.
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
          LifecycleStage.ImmediateAsync, cancellationToken, processingMode);
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
  private async Task _firePostLifecycleAsync(
      Dictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> batchProcessedEvents,
      ILifecycleCoordinator? lifecycleCoordinator,
      IReceptorInvoker? receptorInvoker,
      List<IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork>> groupedWork,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken) {

    if (batchProcessedEvents.Count == 0) {
      return;
    }

    if (lifecycleCoordinator is not null) {
      await _firePostLifecycleWithCoordinatorAsync(
        batchProcessedEvents, lifecycleCoordinator, groupedWork, scopedProvider, cancellationToken);
    } else if (receptorInvoker is not null) {
      await _firePostLifecycleFallbackAsync(
        batchProcessedEvents, receptorInvoker, scopedProvider, cancellationToken);
    }
  }

  /// <summary>
  /// Fires PostLifecycle via coordinator with WhenAll gate and stage guards.
  /// Registers expected perspective completions, replays signals, and advances lifecycle stages.
  /// </summary>
  private async Task _firePostLifecycleWithCoordinatorAsync(
      Dictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> batchProcessedEvents,
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
    foreach (var group in groupedWork) {
      var gPerspectiveName = group.Key.PerspectiveName;
      foreach (var (eventId, _) in batchProcessedEvents.Where(e => e.Value.StreamId == group.Key.StreamId)) {
        lifecycleCoordinator.SignalPerspectiveComplete(eventId, gPerspectiveName);
      }
    }

    foreach (var (eventId, (envelope, _)) in batchProcessedEvents) {
      // WhenAll gate: PostAllPerspectives fires only when all perspectives signaled complete
      if (!lifecycleCoordinator.AreAllPerspectivesComplete(eventId)) {
        // Not all perspectives have completed yet — keep tracking alive for next batch.
        // Don't abandon: the tracking instance preserves the stage guard so
        // PostAllPerspectivesAsync fires exactly once across all batch cycles.
        continue;
      }

      await _establishSecurityContextAsync(envelope, scopedProvider, cancellationToken);

      // Get existing tracking (created during PrePerspective via BeginTracking/GetOrAdd)
      var tracking = lifecycleCoordinator.GetTracking(eventId);
      if (tracking is not null) {
        // PostAllPerspectives: fires once per event after ALL perspectives complete (new stage)
        await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesAsync, scopedProvider, cancellationToken);
        await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesInline, scopedProvider, cancellationToken);
        coordinatorMetrics?.PostAllPerspectivesFired.Add(1);

        // PostLifecycle: fires once per event as the final lifecycle stage
        await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scopedProvider, cancellationToken);
        await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scopedProvider, cancellationToken);
        coordinatorMetrics?.PostLifecycleFired.Add(1);
      }

      // DON'T abandon tracking after stages fire — the tracking instance's stage guard
      // prevents PostAllPerspectivesAsync from firing again in subsequent batch cycles.
      // The tracking is marked _isComplete after PostLifecycleInline (see LifecycleTrackingState),
      // so all future AdvanceToAsync calls return immediately.
      // Memory cleanup happens naturally as events age out of batchProcessedEvents.
    }
  }

  /// <summary>
  /// Fallback: direct invocation of PostLifecycle when coordinator is not registered (no WhenAll guarantee).
  /// </summary>
  private static async Task _firePostLifecycleFallbackAsync(
      Dictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> batchProcessedEvents,
      IReceptorInvoker receptorInvoker,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken) {

    foreach (var (_, (envelope, streamId)) in batchProcessedEvents) {
      var context = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PostLifecycleAsync,
        StreamId = streamId,
        PerspectiveType = null,
        MessageSource = MessageSource.Local,
        AttemptNumber = 1
      };

      await _establishSecurityContextAsync(envelope, scopedProvider, cancellationToken);
      await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PostLifecycleAsync, context, cancellationToken);
      await receptorInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync,
        context with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);
      await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PostLifecycleInline,
        context with { CurrentStage = LifecycleStage.PostLifecycleInline }, cancellationToken);
      await receptorInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync,
        context with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);
    }
  }

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
  /// Loads once and reuses for both PostPerspectiveAsync and PostPerspectiveInline stages.
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
  /// Used for both PostPerspectiveAsync (before checkpoint save) and PostPerspectiveInline (after checkpoint save).
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
      MessageEnvelope<IEvent> envelope,
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
  /// Retry configuration for completion acknowledgement.
  /// Controls exponential backoff when ProcessWorkBatchAsync fails.
  /// </summary>
  public WorkerRetryOptions RetryOptions { get; set; } = new();
}
