using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background worker that processes perspective checkpoints using IWorkCoordinator.
/// Polls for event store streams with new events since last checkpoint,
/// invokes perspectives, and tracks checkpoint progress per stream.
/// Uses lease-based coordination for reliable perspective processing across instances.
/// </summary>
/// <docs>workers/perspective-worker</docs>
public partial class PerspectiveWorker(
  IServiceInstanceProvider instanceProvider,
  IServiceScopeFactory scopeFactory,
  IOptions<PerspectiveWorkerOptions> options,
  IPerspectiveCompletionStrategy? completionStrategy = null,
  IDatabaseReadinessCheck? databaseReadinessCheck = null,
  ILifecycleInvoker? lifecycleInvoker = null,
  IEventStore? eventStore = null,
  ILogger<PerspectiveWorker>? logger = null
) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IDatabaseReadinessCheck _databaseReadinessCheck = databaseReadinessCheck ?? new DefaultDatabaseReadinessCheck();
  private readonly ILifecycleInvoker? _lifecycleInvoker = lifecycleInvoker;
  private readonly IEventStore? _eventStore = eventStore;
  private readonly ILogger<PerspectiveWorker> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PerspectiveWorker>.Instance;
  private readonly PerspectiveWorkerOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
  private readonly IPerspectiveCompletionStrategy _completionStrategy = completionStrategy ?? new BatchedCompletionStrategy(
    retryTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.RetryTimeoutSeconds),
    backoffMultiplier: (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.EnableExponentialBackoff
      ? (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.BackoffMultiplier
      : 1.0,
    maxTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.MaxBackoffSeconds)
  );

  // Metrics tracking
  private int _consecutiveDatabaseNotReadyChecks;
  private int _consecutiveEmptyPolls;
  private bool _isIdle = true;  // Start in idle state

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

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogWorkerStarting(_logger, _instanceProvider.InstanceId, _instanceProvider.ServiceName, _instanceProvider.HostName, _instanceProvider.ProcessId, _options.PollingIntervalMilliseconds);

    // Process any pending perspective checkpoints IMMEDIATELY on startup (before first polling delay)
    try {
      LogCheckingPendingCheckpoints(_logger);
      var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
      if (isDatabaseReady) {
        await _processWorkBatchAsync(stoppingToken);
        LogInitialCheckpointProcessingComplete(_logger);
      } else {
        LogDatabaseNotReadyOnStartup(_logger);
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogErrorProcessingInitialCheckpoints(_logger, ex);
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        // Check database readiness before attempting work coordinator call
        var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
        if (!isDatabaseReady) {
          // Database not ready - skip ProcessWorkBatchAsync
          Interlocked.Increment(ref _consecutiveDatabaseNotReadyChecks);

          // Log at Information level (important operational event)
          LogDatabaseNotReady(_logger, _consecutiveDatabaseNotReadyChecks);

          // Warn if database has been continuously unavailable
          if (_consecutiveDatabaseNotReadyChecks > 10) {
            LogDatabaseNotReadyWarning(_logger, _consecutiveDatabaseNotReadyChecks);
          }

          // Wait before retry
          await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
          continue;
        }

        // Database is ready - reset consecutive counter
        Interlocked.Exchange(ref _consecutiveDatabaseNotReadyChecks, 0);

        await _processWorkBatchAsync(stoppingToken);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogErrorProcessingCheckpoints(_logger, ex);
      }

      // Wait before next poll (unless cancelled)
      try {
        await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
      } catch (OperationCanceledException) {
        // Graceful shutdown
        break;
      }
    }

    LogWorkerStopping(_logger);
  }

  private async Task _processWorkBatchAsync(CancellationToken cancellationToken) {
    // Create a scope to resolve scoped IWorkCoordinator
    await using var scope = _scopeFactory.CreateAsyncScope();
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    // 1. Get pending items (status = Pending, not yet sent)
    var pendingCompletions = _completionStrategy.GetPendingCompletions();
    var pendingFailures = _completionStrategy.GetPendingFailures();

    // 2. Extract actual completion data for ProcessWorkBatchAsync
    var completionsToSend = pendingCompletions.Select(tc => tc.Completion).ToArray();
    var failuresToSend = pendingFailures.Select(tc => tc.Completion).ToArray();

    // 3. Mark as Sent BEFORE calling ProcessWorkBatchAsync
    var sentAt = DateTimeOffset.UtcNow;
    _completionStrategy.MarkAsSent(pendingCompletions, pendingFailures, sentAt);

    // 4. Call ProcessWorkBatchAsync (may throw or return partial acknowledgement)
    WorkBatch workBatch;
    try {
      workBatch = await workCoordinator.ProcessWorkBatchAsync(
        _instanceProvider.InstanceId,
        _instanceProvider.ServiceName,
        _instanceProvider.HostName,
        _instanceProvider.ProcessId,
        metadata: _options.InstanceMetadata,
        outboxCompletions: [],
        outboxFailures: [],
        inboxCompletions: [],
        inboxFailures: [],
        receptorCompletions: [],
        receptorFailures: [],
        perspectiveCompletions: completionsToSend,
        perspectiveFailures: failuresToSend,
        newOutboxMessages: [],
        newInboxMessages: [],
        renewOutboxLeaseIds: [],
        renewInboxLeaseIds: [],
        flags: _options.DebugMode ? WorkBatchFlags.DebugMode : WorkBatchFlags.None,
        partitionCount: _options.PartitionCount,
        leaseSeconds: _options.LeaseSeconds,
        staleThresholdSeconds: _options.StaleThresholdSeconds,
        cancellationToken: cancellationToken
      );
    } catch (Exception ex) {
      // Database failure: Completions remain in 'Sent' status
      // ResetStale() will move them back to 'Pending' after timeout
      LogErrorProcessingWorkBatch(_logger, ex);
      return; // Exit early, retry on next cycle
    }

    // 5. Extract acknowledgement counts from workBatch metadata (from first row)
    var completionsProcessed = 0;
    var failuresProcessed = 0;

    // Check perspective work first
    var perspectiveFirstRow = workBatch.PerspectiveWork.FirstOrDefault();
    if (perspectiveFirstRow?.Metadata != null) {
      if (perspectiveFirstRow.Metadata.TryGetValue("perspective_completions_processed", out var compCount)) {
        completionsProcessed = compCount.GetInt32();
      }
      if (perspectiveFirstRow.Metadata.TryGetValue("perspective_failures_processed", out var failCount)) {
        failuresProcessed = failCount.GetInt32();
      }
    } else {
      // Check outbox work if no perspective work
      var outboxFirstRow = workBatch.OutboxWork.FirstOrDefault();
      if (outboxFirstRow?.Metadata != null) {
        if (outboxFirstRow.Metadata.TryGetValue("perspective_completions_processed", out var compCount)) {
          completionsProcessed = compCount.GetInt32();
        }
        if (outboxFirstRow.Metadata.TryGetValue("perspective_failures_processed", out var failCount)) {
          failuresProcessed = failCount.GetInt32();
        }
      } else {
        // Check inbox work if no outbox work
        var inboxFirstRow = workBatch.InboxWork.FirstOrDefault();
        if (inboxFirstRow?.Metadata != null) {
          if (inboxFirstRow.Metadata.TryGetValue("perspective_completions_processed", out var compCount)) {
            completionsProcessed = compCount.GetInt32();
          }
          if (inboxFirstRow.Metadata.TryGetValue("perspective_failures_processed", out var failCount)) {
            failuresProcessed = failCount.GetInt32();
          }
        }
      }
    }

    // 6. Mark as Acknowledged based on counts from SQL
    _completionStrategy.MarkAsAcknowledged(completionsProcessed, failuresProcessed);

    // 7. Clear only Acknowledged items
    _completionStrategy.ClearAcknowledged();

    // 8. Reset stale items (sent but not acknowledged for > timeout) back to Pending
    _completionStrategy.ResetStale(DateTimeOffset.UtcNow);

    // Group perspective work items by (StreamId, PerspectiveName)
    // Each work item represents a single event, but the runner processes ALL events for a stream
    // So we only call RunAsync() ONCE per (stream, perspective) pair
    var groupedWork = workBatch.PerspectiveWork
      .GroupBy(w => new { w.StreamId, w.PerspectiveName })
      .ToList();

    // Process perspective work using IPerspectiveRunner (once per stream/perspective group)
    foreach (var group in groupedWork) {
      var streamId = group.Key.StreamId;
      var perspectiveName = group.Key.PerspectiveName;
      var workItems = group.ToList();

      try {
        // Look up the checkpoint to get the LastProcessedEventId
        // This tells the runner where to start reading events from
        var checkpoint = await workCoordinator.GetPerspectiveCheckpointAsync(
          streamId,
          perspectiveName,
          cancellationToken
        );

        var lastProcessedEventId = checkpoint?.LastEventId;

        LogProcessingPerspectiveCheckpoint(_logger, perspectiveName, streamId, lastProcessedEventId?.ToString() ?? "null (never processed)");

        // Resolve the generated IPerspectiveRunner for this perspective
        var registry = scope.ServiceProvider.GetService<IPerspectiveRunnerRegistry>();
        if (registry == null) {
          LogPerspectiveRunnerRegistryNotRegistered(_logger, perspectiveName);
          continue;
        }

        // DIAGNOSTIC: Log registry resolution details
        LogRunnerRegistryResolved(_logger, perspectiveName, registry.GetType().FullName ?? "unknown", registry.GetHashCode());

        var runner = registry.GetRunner(perspectiveName, scope.ServiceProvider);
        if (runner == null) {
          LogNoPerspectiveRunnerFound(_logger, perspectiveName);
          continue;
        }

        // DIAGNOSTIC: Log runner resolution details
        LogRunnerInstanceResolved(_logger, perspectiveName, runner.GetType().FullName ?? "unknown", runner.GetHashCode());

        // Invoke runner to process ALL events for this stream/perspective
        // The runner will read from lastProcessedEventId onwards and process all available events
        var result = await runner.RunAsync(
          streamId,
          perspectiveName,
          lastProcessedEventId,
          cancellationToken
        );

        // Phase 3: Invoke lifecycle receptors after perspective processing completes
        // This enables deterministic test synchronization and eliminates race conditions
        if (_lifecycleInvoker is not null && _eventStore is not null && result.Status == PerspectiveProcessingStatus.Completed) {
          await _invokeLifecycleReceptorsAsync(
            streamId,
            perspectiveName,
            lastProcessedEventId,
            result.LastEventId,
            cancellationToken
          );
        }

        // Report completion via strategy
        await _completionStrategy.ReportCompletionAsync(result, workCoordinator, cancellationToken);

        LogPerspectiveCheckpointCompleted(_logger, perspectiveName, streamId, result.LastEventId);
      } catch (Exception ex) {
        LogErrorProcessingPerspectiveCheckpoint(_logger, ex, perspectiveName, streamId);

        var failure = new PerspectiveCheckpointFailure {
          StreamId = streamId,
          PerspectiveName = perspectiveName,
          LastEventId = Guid.Empty, // We don't know which event failed
          Status = PerspectiveProcessingStatus.Failed,
          Error = ex.Message
        };

        // Report failure via strategy
        await _completionStrategy.ReportFailureAsync(failure, workCoordinator, cancellationToken);
      }
    }

    // Log a summary of perspective processing activity
    int totalActivity = completionsToSend.Length + failuresToSend.Length + workBatch.PerspectiveWork.Count;
    if (totalActivity > 0) {
      LogPerspectiveBatchSummary(_logger, workBatch.PerspectiveWork.Count, completionsToSend.Length, failuresToSend.Length);
    } else {
      LogNoWorkClaimed(_logger);
    }

    // Track work state transitions for OnWorkProcessingStarted / OnWorkProcessingIdle callbacks
    bool hasWork = workBatch.PerspectiveWork.Count > 0;

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

      // Check if should transition to idle
      if (!_isIdle && _consecutiveEmptyPolls >= _options.IdleThresholdPolls) {
        _isIdle = true;
        OnWorkProcessingIdle?.Invoke();
        LogPerspectiveProcessingIdle(_logger, _consecutiveEmptyPolls);
      }
    }
  }

  /// <summary>
  /// Invokes lifecycle receptors after perspective processing completes successfully.
  /// Loads events that were just processed and notifies receptors at PostPerspectiveAsync
  /// and PostPerspectiveInline stages for deterministic test synchronization.
  /// </summary>
  private async Task _invokeLifecycleReceptorsAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      Guid currentEventId,
      CancellationToken cancellationToken) {

    if (_eventStore is null || _lifecycleInvoker is null) {
      return; // Guards against nullability - should never happen if called from RunAsync
    }

    try {
      // Load all events that were just processed by this perspective run
      // Use polymorphic read since we don't know the concrete event types
      var processedEvents = await _eventStore.GetEventsBetweenAsync<IEvent>(
        streamId,
        lastProcessedEventId,  // Exclusive start
        currentEventId,        // Inclusive end
        cancellationToken
      );

      if (processedEvents.Count == 0) {
        return; // No events to process
      }

      // Create lifecycle context with stream and perspective information
      var context = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PostPerspectiveAsync, // Will be updated per stage
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastProcessedEventId = currentEventId
      };

      // Phase 1: PostPerspectiveAsync (non-blocking, informational)
      // Receptors at this stage should not block perspective processing
      context = context with { CurrentStage = LifecycleStage.PostPerspectiveAsync };
      foreach (var envelope in processedEvents) {
        await _lifecycleInvoker.InvokeAsync(
          envelope.Payload,
          LifecycleStage.PostPerspectiveAsync,
          context,
          cancellationToken
        );
      }

      // Phase 2: PostPerspectiveInline (blocking, for test synchronization)
      // Receptors at this stage fire BEFORE checkpoint completion is reported
      // This enables deterministic test synchronization without polling
      context = context with { CurrentStage = LifecycleStage.PostPerspectiveInline };
      foreach (var envelope in processedEvents) {
        await _lifecycleInvoker.InvokeAsync(
          envelope.Payload,
          LifecycleStage.PostPerspectiveInline,
          context,
          cancellationToken
        );
      }

    } catch (Exception ex) {
      // Log error but don't fail the entire perspective processing
      // Lifecycle receptor failures shouldn't prevent checkpoint progress
      LogErrorInvokingLifecycleReceptors(_logger, ex, perspectiveName, streamId);
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
    Message = "Checking for pending perspective checkpoints on startup..."
  )]
  static partial void LogCheckingPendingCheckpoints(ILogger logger);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Debug,
    Message = "Initial perspective checkpoint processing complete"
  )]
  static partial void LogInitialCheckpointProcessingComplete(ILogger logger);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Warning,
    Message = "Database not ready on startup - skipping initial perspective checkpoint processing"
  )]
  static partial void LogDatabaseNotReadyOnStartup(ILogger logger);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Error,
    Message = "Error processing initial perspective checkpoints on startup"
  )]
  static partial void LogErrorProcessingInitialCheckpoints(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Information,
    Message = "Database not ready, skipping perspective checkpoint processing (consecutive checks: {ConsecutiveCount})"
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
    Message = "Error processing perspective checkpoints"
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
    Message = "Processing perspective checkpoint: {PerspectiveName} for stream {StreamId}, last processed event: {LastProcessedEventId}"
  )]
  static partial void LogProcessingPerspectiveCheckpoint(ILogger logger, string perspectiveName, Guid streamId, string lastProcessedEventId);

  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Error,
    Message = "IPerspectiveRunnerRegistry not registered. Call AddPerspectiveRunners() in service registration. Skipping perspective: {PerspectiveName}"
  )]
  static partial void LogPerspectiveRunnerRegistryNotRegistered(ILogger logger, string perspectiveName);

  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Warning,
    Message = "No IPerspectiveRunner found for perspective {PerspectiveName}. Ensure perspective implements IPerspectiveFor<TModel, TEvent> and has [StreamKey] on model. Skipping."
  )]
  static partial void LogNoPerspectiveRunnerFound(ILogger logger, string perspectiveName);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Debug,
    Message = "Perspective checkpoint completed: {PerspectiveName} for stream {StreamId}, last event: {LastEventId}"
  )]
  static partial void LogPerspectiveCheckpointCompleted(ILogger logger, string perspectiveName, Guid streamId, Guid lastEventId);

  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Error,
    Message = "Error processing perspective checkpoint: {PerspectiveName} for stream {StreamId}"
  )]
  static partial void LogErrorProcessingPerspectiveCheckpoint(ILogger logger, Exception ex, string perspectiveName, Guid streamId);

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
}

/// <summary>
/// Configuration options for the Perspective worker.
/// </summary>
public class PerspectiveWorkerOptions {
  /// <summary>
  /// Milliseconds to wait between polling for perspective checkpoint work.
  /// Default: 1000 (1 second)
  /// </summary>
  public int PollingIntervalMilliseconds { get; set; } = 1000;

  /// <summary>
  /// Lease duration in seconds.
  /// Perspective checkpoints claimed will be locked for this duration.
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
