using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background worker that processes perspective checkpoints using IWorkCoordinator.
/// Polls for event store streams with new events since last checkpoint,
/// invokes perspectives, and tracks checkpoint progress per stream.
/// Uses lease-based coordination for reliable perspective processing across instances.
/// </summary>
public class PerspectiveWorker(
  IServiceInstanceProvider instanceProvider,
  IServiceScopeFactory scopeFactory,
  IOptions<PerspectiveWorkerOptions> options,
  IDatabaseReadinessCheck? databaseReadinessCheck = null,
  ILogger<PerspectiveWorker>? logger = null
) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IDatabaseReadinessCheck _databaseReadinessCheck = databaseReadinessCheck ?? new DefaultDatabaseReadinessCheck();
  private readonly ILogger<PerspectiveWorker> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PerspectiveWorker>.Instance;
  private readonly PerspectiveWorkerOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

  // Bags for collecting perspective processing results (completions, failures)
  private readonly ConcurrentBag<PerspectiveCheckpointCompletion> _completions = new();
  private readonly ConcurrentBag<PerspectiveCheckpointFailure> _failures = new();

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
    _logger.LogInformation(
      "Perspective worker starting: Instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}), interval: {Interval}ms",
      _instanceProvider.InstanceId,
      _instanceProvider.ServiceName,
      _instanceProvider.HostName,
      _instanceProvider.ProcessId,
      _options.PollingIntervalMilliseconds
    );

    // Process any pending perspective checkpoints IMMEDIATELY on startup (before first polling delay)
    try {
      _logger.LogDebug("Checking for pending perspective checkpoints on startup...");
      var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
      if (isDatabaseReady) {
        await ProcessWorkBatchAsync(stoppingToken);
        _logger.LogDebug("Initial perspective checkpoint processing complete");
      } else {
        _logger.LogWarning("Database not ready on startup - skipping initial perspective checkpoint processing");
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      _logger.LogError(ex, "Error processing initial perspective checkpoints on startup");
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        // Check database readiness before attempting work coordinator call
        var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
        if (!isDatabaseReady) {
          // Database not ready - skip ProcessWorkBatchAsync
          Interlocked.Increment(ref _consecutiveDatabaseNotReadyChecks);

          // Log at Information level (important operational event)
          _logger.LogInformation(
            "Database not ready, skipping perspective checkpoint processing (consecutive checks: {ConsecutiveCount})",
            _consecutiveDatabaseNotReadyChecks
          );

          // Warn if database has been continuously unavailable
          if (_consecutiveDatabaseNotReadyChecks > 10) {
            _logger.LogWarning(
              "Database not ready for {ConsecutiveCount} consecutive polling cycles. Perspective worker is paused.",
              _consecutiveDatabaseNotReadyChecks
            );
          }

          // Wait before retry
          await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
          continue;
        }

        // Database is ready - reset consecutive counter
        Interlocked.Exchange(ref _consecutiveDatabaseNotReadyChecks, 0);

        await ProcessWorkBatchAsync(stoppingToken);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        _logger.LogError(ex, "Error processing perspective checkpoints");
      }

      // Wait before next poll (unless cancelled)
      try {
        await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
      } catch (OperationCanceledException) {
        // Graceful shutdown
        break;
      }
    }

    _logger.LogInformation("Perspective worker stopping");
  }

  private async Task ProcessWorkBatchAsync(CancellationToken cancellationToken) {
    // Create a scope to resolve scoped IWorkCoordinator
    using var scope = _scopeFactory.CreateScope();
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    // Collect accumulated results from perspective processing
    var perspectiveCompletions = _completions.ToArray();
    var perspectiveFailures = _failures.ToArray();
    _completions.Clear();
    _failures.Clear();

    // Get work batch (heartbeat, claim perspective work, return for processing)
    // Each call:
    // - Reports previous perspective processing results (if any)
    // - Registers/updates instance + heartbeat
    // - Cleans up stale instances
    // - Claims perspective checkpoint work via modulo-based partition distribution
    // - Returns perspective checkpoint work for this instance to process
    var workBatch = await workCoordinator.ProcessWorkBatchAsync(
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
      perspectiveCompletions: perspectiveCompletions,
      perspectiveFailures: perspectiveFailures,
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

    // Process perspective work (Stage 6: skeleton implementation)
    // TODO (Stage 7+): Implement actual event loading and perspective invocation
    // For now, log what was claimed and immediately mark as completed
    foreach (var perspectiveWork in workBatch.PerspectiveWork) {
      try {
        _logger.LogInformation(
          "Processing perspective checkpoint: {PerspectiveName} for stream {StreamId}, last processed event: {LastProcessedEventId}",
          perspectiveWork.PerspectiveName,
          perspectiveWork.StreamId,
          perspectiveWork.LastProcessedEventId?.ToString() ?? "null (never processed)"
        );

        // TODO (Stage 7+): Load events from event store starting from LastProcessedEventId
        // TODO (Stage 7+): Invoke perspective.Update() for each event
        // For now, just mark as completed immediately (skeleton behavior)

        _completions.Add(new PerspectiveCheckpointCompletion {
          StreamId = perspectiveWork.StreamId,
          PerspectiveName = perspectiveWork.PerspectiveName,
          Status = PerspectiveProcessingStatus.Completed,
          LastEventId = perspectiveWork.LastProcessedEventId ?? Guid.Empty  // Placeholder
        });

        _logger.LogDebug(
          "Perspective checkpoint marked as completed (skeleton): {PerspectiveName} for stream {StreamId}",
          perspectiveWork.PerspectiveName,
          perspectiveWork.StreamId
        );
      } catch (Exception ex) {
        _logger.LogError(
          ex,
          "Error processing perspective checkpoint: {PerspectiveName} for stream {StreamId}",
          perspectiveWork.PerspectiveName,
          perspectiveWork.StreamId
        );

        _failures.Add(new PerspectiveCheckpointFailure {
          StreamId = perspectiveWork.StreamId,
          PerspectiveName = perspectiveWork.PerspectiveName,
          LastEventId = perspectiveWork.LastProcessedEventId ?? Guid.Empty,
          Status = PerspectiveProcessingStatus.Failed,
          Error = ex.Message
        });
      }
    }

    // Log a summary of perspective processing activity
    int totalActivity = perspectiveCompletions.Length + perspectiveFailures.Length + workBatch.PerspectiveWork.Count;
    if (totalActivity > 0) {
      _logger.LogInformation(
        "Perspective batch: Claimed={Claimed}, completed={Completed}, failed={Failed}",
        workBatch.PerspectiveWork.Count,
        perspectiveCompletions.Length,
        perspectiveFailures.Length
      );
    } else {
      _logger.LogDebug("Perspective checkpoint processing: no work claimed");
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
        _logger.LogDebug("Perspective processing started (idle → active)");
      }
    } else {
      // Increment empty poll counter
      Interlocked.Increment(ref _consecutiveEmptyPolls);

      // Check if should transition to idle
      if (!_isIdle && _consecutiveEmptyPolls >= _options.IdleThresholdPolls) {
        _isIdle = true;
        OnWorkProcessingIdle?.Invoke();
        _logger.LogDebug("Perspective processing idle (active → idle) after {EmptyPolls} empty polls", _consecutiveEmptyPolls);
      }
    }
  }
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
  public bool DebugMode { get; set; } = false;

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
}
