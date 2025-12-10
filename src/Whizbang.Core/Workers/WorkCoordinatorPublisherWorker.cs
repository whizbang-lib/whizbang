using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background worker that uses IWorkCoordinator for lease-based coordination.
/// Uses shared channel for async message publishing with concurrent processing.
/// The channel is shared with ScopedWorkCoordinatorStrategy to enable immediate
/// processing of work returned from process_work_batch.
/// Performs all operations in a single atomic call:
/// - Registers/updates instance with heartbeat
/// - Cleans up stale instances
/// - Marks completed/failed messages
/// - Claims and processes orphaned work (outbox and inbox)
/// </summary>
public class WorkCoordinatorPublisherWorker(
  IServiceInstanceProvider instanceProvider,
  IServiceScopeFactory scopeFactory,
  IMessagePublishStrategy publishStrategy,
  WorkChannelWriter workChannelWriter,
  IDatabaseReadinessCheck? databaseReadinessCheck = null,
  WorkCoordinatorPublisherOptions? options = null,
  ILogger<WorkCoordinatorPublisherWorker>? logger = null
) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IMessagePublishStrategy _publishStrategy = publishStrategy ?? throw new ArgumentNullException(nameof(publishStrategy));
  private readonly WorkChannelWriter _workChannelWriter = workChannelWriter ?? throw new ArgumentNullException(nameof(workChannelWriter));
  private readonly IDatabaseReadinessCheck _databaseReadinessCheck = databaseReadinessCheck ?? new DefaultDatabaseReadinessCheck();
  private readonly ILogger<WorkCoordinatorPublisherWorker> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorPublisherWorker>.Instance;
  private readonly WorkCoordinatorPublisherOptions _options = options ?? new WorkCoordinatorPublisherOptions();

  // Bags for collecting publish results (completions, failures, lease renewals)
  private readonly ConcurrentBag<MessageCompletion> _completions = new();
  private readonly ConcurrentBag<MessageFailure> _failures = new();
  private readonly ConcurrentBag<Guid> _leaseRenewals = new();

  // Metrics tracking
  private int _consecutiveNotReadyChecks;
  private int _consecutiveDatabaseNotReadyChecks;
  private long _totalLeaseRenewals;
  private long _totalBufferedMessages;

  /// <summary>
  /// Gets the number of consecutive times the transport was not ready.
  /// Resets to 0 when transport becomes ready.
  /// </summary>
  public int ConsecutiveNotReadyChecks => _consecutiveNotReadyChecks;

  /// <summary>
  /// Gets the number of consecutive times the database was not ready.
  /// Resets to 0 when database becomes ready.
  /// </summary>
  public int ConsecutiveDatabaseNotReadyChecks => _consecutiveDatabaseNotReadyChecks;

  /// <summary>
  /// Gets the total count of messages buffered due to transport not being ready.
  /// Accumulates across all batches.
  /// </summary>
  public long BufferedMessageCount => _totalBufferedMessages;

  /// <summary>
  /// Gets the total number of lease renewals performed since worker started.
  /// Accumulates across all batches.
  /// </summary>
  public long TotalLeaseRenewals => _totalLeaseRenewals;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation(
      "WorkCoordinator publisher starting: Instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}), interval: {Interval}ms",
      _instanceProvider.InstanceId,
      _instanceProvider.ServiceName,
      _instanceProvider.HostName,
      _instanceProvider.ProcessId,
      _options.PollingIntervalMilliseconds
    );

    // Start both loops concurrently
    var coordinatorTask = CoordinatorLoopAsync(stoppingToken);
    var publisherTask = PublisherLoopAsync(stoppingToken);

    // Wait for both to complete
    await Task.WhenAll(coordinatorTask, publisherTask);

    _logger.LogInformation("WorkCoordinator publisher stopping");
  }

  private async Task CoordinatorLoopAsync(CancellationToken stoppingToken) {
    // Process any pending outbox messages IMMEDIATELY on startup (before first polling delay)
    // This ensures seeded or pre-existing messages are published right away
    try {
      _logger.LogDebug("Checking for pending outbox messages on startup...");
      var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
      if (isDatabaseReady) {
        await ProcessWorkBatchAsync(stoppingToken);
        _logger.LogDebug("Initial work batch processing complete");
      } else {
        _logger.LogWarning("Database not ready on startup - skipping initial work batch processing");
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      _logger.LogError(ex, "Error processing initial work batch on startup");
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        // Check database readiness before attempting work coordinator call
        var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
        if (!isDatabaseReady) {
          // Database not ready - skip ProcessWorkBatchAsync, keep buffering in memory
          Interlocked.Increment(ref _consecutiveDatabaseNotReadyChecks);

          // Log at Information level (important operational event)
          _logger.LogInformation(
            "Database not ready, skipping work batch processing (consecutive checks: {ConsecutiveCount})",
            _consecutiveDatabaseNotReadyChecks
          );

          // Warn if database has been continuously unavailable
          if (_consecutiveDatabaseNotReadyChecks > 10) {
            _logger.LogWarning(
              "Database not ready for {ConsecutiveCount} consecutive polling cycles. Work coordinator is paused.",
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
        _logger.LogError(ex, "Error processing work batch");
      }

      // Wait before next poll (unless cancelled)
      try {
        await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
      } catch (OperationCanceledException) {
        // Graceful shutdown
        break;
      }
    }

    // Signal publisher loop to finish
    _workChannelWriter.Complete();
  }

  private async Task PublisherLoopAsync(CancellationToken stoppingToken) {
    await foreach (var work in _workChannelWriter.Reader.ReadAllAsync(stoppingToken)) {
      try {
        // Check transport readiness before attempting publish
        var isReady = await _publishStrategy.IsReadyAsync(stoppingToken);
        if (!isReady) {
          // Transport not ready - renew lease to buffer message
          _leaseRenewals.Add(work.MessageId);
          Interlocked.Increment(ref _consecutiveNotReadyChecks);
          Interlocked.Increment(ref _totalLeaseRenewals);
          Interlocked.Increment(ref _totalBufferedMessages);

          // Log at Information level (important operational event)
          _logger.LogInformation(
            "Transport not ready, buffering message {MessageId} (destination: {Destination})",
            work.MessageId,
            work.Destination
          );

          // Warn if transport has been continuously unavailable
          if (_consecutiveNotReadyChecks > 10) {
            _logger.LogWarning(
              "Transport not ready for {ConsecutiveCount} consecutive messages. Messages are being buffered with lease renewal.",
              _consecutiveNotReadyChecks
            );
          }

          continue;
        }

        // Transport is ready - reset consecutive counter
        Interlocked.Exchange(ref _consecutiveNotReadyChecks, 0);

        // Publish via strategy
        var result = await _publishStrategy.PublishAsync(work, stoppingToken);

        // Collect results
        if (result.Success) {
          _completions.Add(new MessageCompletion {
            MessageId = work.MessageId,
            Status = result.CompletedStatus
          });
        } else {
          _failures.Add(new MessageFailure {
            MessageId = work.MessageId,
            CompletedStatus = result.CompletedStatus,
            Error = result.Error ?? "Unknown error",
            Reason = result.Reason
          });

          // Still log individual errors for debugging
          _logger.LogError(
            "Failed to publish outbox message {MessageId} to {Destination}: {Error} (Reason: {Reason})",
            work.MessageId,
            work.Destination,
            result.Error,
            result.Reason
          );
        }
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        _logger.LogError(
          ex,
          "Unexpected error publishing outbox message {MessageId}",
          work.MessageId
        );

        _failures.Add(new MessageFailure {
          MessageId = work.MessageId,
          CompletedStatus = work.Status,
          Error = ex.Message,
          Reason = MessageFailureReason.Unknown
        });
      }
    }
  }

  private async Task ProcessWorkBatchAsync(CancellationToken cancellationToken) {
    // Create a scope to resolve scoped IWorkCoordinator
    using var scope = _scopeFactory.CreateScope();
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    // Collect accumulated results from publisher loop
    var outboxCompletions = _completions.ToArray();
    var outboxFailures = _failures.ToArray();
    var renewOutboxLeaseIds = _leaseRenewals.ToArray();
    _completions.Clear();
    _failures.Clear();
    _leaseRenewals.Clear();

    // Get work batch (heartbeat, claim work, return for processing)
    // Each call:
    // - Reports previous results (if any)
    // - Registers/updates instance + heartbeat
    // - Cleans up stale instances
    // - Claims orphaned work via modulo-based partition distribution
    // - Renews leases for buffered messages awaiting transport readiness
    // - Returns work for this instance to process
    var workBatch = await workCoordinator.ProcessWorkBatchAsync(
      _instanceProvider.InstanceId,
      _instanceProvider.ServiceName,
      _instanceProvider.HostName,
      _instanceProvider.ProcessId,
      metadata: _options.InstanceMetadata,
      outboxCompletions: outboxCompletions,
      outboxFailures: outboxFailures,
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],  // TODO: Add receptor processing support
      receptorFailures: [],
      perspectiveCompletions: [],  // TODO: Add perspective checkpoint support
      perspectiveFailures: [],
      newOutboxMessages: [],  // Not used in publisher worker (dispatcher handles new messages)
      newInboxMessages: [],   // Not used in publisher worker (consumer handles new messages)
      renewOutboxLeaseIds: renewOutboxLeaseIds,
      renewInboxLeaseIds: [],
      flags: WorkBatchFlags.None,
      partitionCount: 10_000,
      maxPartitionsPerInstance: 100,
      leaseSeconds: _options.LeaseSeconds,
      staleThresholdSeconds: _options.StaleThresholdSeconds,
      cancellationToken: cancellationToken
    );

    // Log a summary of message processing activity
    int totalActivity = outboxCompletions.Length + outboxFailures.Length + renewOutboxLeaseIds.Length + workBatch.OutboxWork.Count + workBatch.InboxWork.Count;
    if (totalActivity > 0) {
      _logger.LogInformation(
        "Message batch: Outbox published={Published}, failed={OutboxFailed}, buffered={Buffered}, claimed={Claimed} | Inbox claimed={InboxClaimed}, failed={InboxFailed}",
        outboxCompletions.Length,
        outboxFailures.Length,
        renewOutboxLeaseIds.Length,
        workBatch.OutboxWork.Count,
        workBatch.InboxWork.Count,
        workBatch.InboxWork.Count  // All inbox currently marked as failed (not yet implemented)
      );
    } else {
      _logger.LogDebug("Work batch processing: no work claimed (all partitions assigned to other instances or no pending messages)");
    }

    // Write outbox work to channel for publisher loop
    if (workBatch.OutboxWork.Count > 0) {
      // Sort by MessageId (UUIDv7 has time-based ordering)
      var orderedOutboxWork = workBatch.OutboxWork.OrderBy(m => m.MessageId).ToList();

      foreach (var work in orderedOutboxWork) {
        await _workChannelWriter.WriteAsync(work, cancellationToken);
      }
    }

    // Process inbox work
    // TODO: Implement inbox processing - requires deserializing to typed messages and invoking receptors
    // For now, mark as failed to prevent infinite retry loops
    if (workBatch.InboxWork.Count > 0) {
      foreach (var inboxMessage in workBatch.InboxWork) {
        _failures.Add(new MessageFailure {
          MessageId = inboxMessage.MessageId,
          CompletedStatus = inboxMessage.Status,  // Preserve what was already completed
          Error = "Inbox processing not yet implemented",
          Reason = MessageFailureReason.Unknown
        });
      }
    }
  }

}


/// <summary>
/// Configuration options for the WorkCoordinator publisher worker.
/// </summary>
public class WorkCoordinatorPublisherOptions {
  /// <summary>
  /// Milliseconds to wait between polling for work.
  /// Default: 1000 (1 second)
  /// </summary>
  public int PollingIntervalMilliseconds { get; set; } = 1000;

  /// <summary>
  /// Lease duration in seconds.
  /// Messages claimed will be locked for this duration.
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
}
