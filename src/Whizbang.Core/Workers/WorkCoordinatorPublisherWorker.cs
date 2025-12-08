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
/// Uses channels for async message publishing with concurrent processing.
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
  WorkCoordinatorPublisherOptions? options = null,
  ILogger<WorkCoordinatorPublisherWorker>? logger = null
) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IMessagePublishStrategy _publishStrategy = publishStrategy ?? throw new ArgumentNullException(nameof(publishStrategy));
  private readonly ILogger<WorkCoordinatorPublisherWorker> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorPublisherWorker>.Instance;
  private readonly WorkCoordinatorPublisherOptions _options = options ?? new WorkCoordinatorPublisherOptions();

  // Channel-based async message processing
  private readonly Channel<OutboxWork> _workChannel = Channel.CreateUnbounded<OutboxWork>();
  private readonly ConcurrentBag<MessageCompletion> _completions = new();
  private readonly ConcurrentBag<MessageFailure> _failures = new();

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
    while (!stoppingToken.IsCancellationRequested) {
      try {
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
    _workChannel.Writer.Complete();
  }

  private async Task PublisherLoopAsync(CancellationToken stoppingToken) {
    await foreach (var work in _workChannel.Reader.ReadAllAsync(stoppingToken)) {
      try {
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
            Error = result.Error ?? "Unknown error"
          });

          // Still log individual errors for debugging
          _logger.LogError(
            "Failed to publish outbox message {MessageId} to {Destination}: {Error}",
            work.MessageId,
            work.Destination,
            result.Error
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
          Error = ex.Message
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
    _completions.Clear();
    _failures.Clear();

    // Get work batch (heartbeat, claim work, return for processing)
    // Each call:
    // - Reports previous results (if any)
    // - Registers/updates instance + heartbeat
    // - Cleans up stale instances
    // - Claims orphaned work via modulo-based partition distribution
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
      newOutboxMessages: [],  // Not used in publisher worker (dispatcher handles new messages)
      newInboxMessages: [],   // Not used in publisher worker (consumer handles new messages)
      leaseSeconds: _options.LeaseSeconds,
      staleThresholdSeconds: _options.StaleThresholdSeconds,
      cancellationToken: cancellationToken
    );

    // Log a summary of message processing activity (only if non-zero)
    int totalActivity = outboxCompletions.Length + outboxFailures.Length + workBatch.OutboxWork.Count + workBatch.InboxWork.Count;
    if (totalActivity > 0) {
      _logger.LogInformation(
        "Message batch: Outbox published={Published}, failed={OutboxFailed}, claimed={Claimed} | Inbox claimed={InboxClaimed}, failed={InboxFailed}",
        outboxCompletions.Length,
        outboxFailures.Length,
        workBatch.OutboxWork.Count,
        workBatch.InboxWork.Count,
        workBatch.InboxWork.Count  // All inbox currently marked as failed (not yet implemented)
      );
    }

    // Write outbox work to channel for publisher loop
    if (workBatch.OutboxWork.Count > 0) {
      // Sort by MessageId (UUIDv7 has time-based ordering)
      var orderedOutboxWork = workBatch.OutboxWork.OrderBy(m => m.MessageId).ToList();

      foreach (var work in orderedOutboxWork) {
        await _workChannel.Writer.WriteAsync(work, cancellationToken);
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
          Error = "Inbox processing not yet implemented"
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
  /// </summary>
  public Dictionary<string, object>? InstanceMetadata { get; set; }
}
