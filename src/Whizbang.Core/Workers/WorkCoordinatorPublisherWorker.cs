using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
/// Performs all operations in a single atomic call:
/// - Registers/updates instance with heartbeat
/// - Cleans up stale instances
/// - Marks completed/failed messages
/// - Claims and processes orphaned work (outbox and inbox)
/// </summary>
public class WorkCoordinatorPublisherWorker(
  IServiceInstanceProvider instanceProvider,
  IServiceScopeFactory scopeFactory,
  ITransport transport,
  JsonSerializerOptions jsonOptions,
  WorkCoordinatorPublisherOptions? options = null,
  ILogger<WorkCoordinatorPublisherWorker>? logger = null
) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly ILogger<WorkCoordinatorPublisherWorker> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorPublisherWorker>.Instance;
  private readonly WorkCoordinatorPublisherOptions _options = options ?? new WorkCoordinatorPublisherOptions();
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));

  // Track results from previous cycle (for next ProcessWorkBatchAsync call)
  private List<Guid> _outboxCompletedIds = [];
  private List<FailedMessage> _outboxFailedMessages = [];
  private List<Guid> _inboxCompletedIds = [];
  private List<FailedMessage> _inboxFailedMessages = [];

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation(
      "WorkCoordinator publisher starting: Instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}), interval: {Interval}ms",
      _instanceProvider.InstanceId,
      _instanceProvider.ServiceName,
      _instanceProvider.HostName,
      _instanceProvider.ProcessId,
      _options.PollingIntervalMilliseconds
    );

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

    _logger.LogInformation("WorkCoordinator publisher stopping");
  }

  private async Task ProcessWorkBatchAsync(CancellationToken cancellationToken) {
    // Create a scope to resolve scoped IWorkCoordinator
    using var scope = _scopeFactory.CreateScope();
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    // Call ProcessWorkBatchAsync with results from previous cycle
    // This atomic call:
    // 1. Registers/updates this instance + heartbeat
    // 2. Cleans up stale instances
    // 3. Marks previous cycle's completed/failed messages
    // 4. Claims and returns orphaned work
    var workBatch = await workCoordinator.ProcessWorkBatchAsync(
      _instanceProvider.InstanceId,
      _instanceProvider.ServiceName,
      _instanceProvider.HostName,
      _instanceProvider.ProcessId,
      metadata: _options.InstanceMetadata,
      outboxCompletedIds: [.. _outboxCompletedIds],
      outboxFailedMessages: [.. _outboxFailedMessages],
      inboxCompletedIds: [.. _inboxCompletedIds],
      inboxFailedMessages: [.. _inboxFailedMessages],
      leaseSeconds: _options.LeaseSeconds,
      staleThresholdSeconds: _options.StaleThresholdSeconds,
      cancellationToken: cancellationToken
    );

    // Clear previous cycle's results
    _outboxCompletedIds.Clear();
    _outboxFailedMessages.Clear();
    _inboxCompletedIds.Clear();
    _inboxFailedMessages.Clear();

    // Process outbox work
    if (workBatch.OutboxWork.Count > 0) {
      _logger.LogInformation("Processing {Count} outbox messages", workBatch.OutboxWork.Count);
      await ProcessOutboxWorkAsync(workBatch.OutboxWork, cancellationToken);
    }

    // Process inbox work
    // TODO: Implement inbox processing - requires deserializing to typed messages and invoking receptors
    // For now, log and mark as failed to prevent infinite retry loops
    if (workBatch.InboxWork.Count > 0) {
      _logger.LogWarning(
        "Found {Count} inbox messages - inbox processing not yet implemented, marking as failed",
        workBatch.InboxWork.Count
      );

      foreach (var inboxMessage in workBatch.InboxWork) {
        _inboxFailedMessages.Add(new FailedMessage {
          MessageId = inboxMessage.MessageId,
          Error = "Inbox processing not yet implemented"
        });
      }
    }

    if (workBatch.OutboxWork.Count == 0 && workBatch.InboxWork.Count == 0) {
      _logger.LogTrace("No work to process");
    }
  }

  private async Task ProcessOutboxWorkAsync(List<OutboxWork> messages, CancellationToken cancellationToken) {
    foreach (var outboxMessage in messages) {
      if (cancellationToken.IsCancellationRequested) {
        break;
      }

      try {
        await PublishMessageAsync(outboxMessage, cancellationToken);
        _outboxCompletedIds.Add(outboxMessage.MessageId);

        _logger.LogDebug(
          "Published outbox message {MessageId} to {Destination}",
          outboxMessage.MessageId,
          outboxMessage.Destination
        );
      } catch (Exception ex) {
        _logger.LogError(
          ex,
          "Failed to publish outbox message {MessageId} to {Destination}",
          outboxMessage.MessageId,
          outboxMessage.Destination
        );

        _outboxFailedMessages.Add(new FailedMessage {
          MessageId = outboxMessage.MessageId,
          Error = ex.Message
        });
      }
    }
  }


  private async Task PublishMessageAsync(OutboxWork outboxMessage, CancellationToken cancellationToken) {
    // Reconstruct the envelope from JSONB columns
    // The envelope was stored as event_data (payload), metadata (envelope properties), scope (security)

    // Deserialize metadata to extract envelope properties
    using var metadataDoc = JsonDocument.Parse(outboxMessage.Metadata);
    var metadataRoot = metadataDoc.RootElement;

    // Extract hops from metadata using AOT-compatible deserialization
    var hops = new List<MessageHop>();
    if (metadataRoot.TryGetProperty("hops", out var hopsElem)) {
      var hopsJson = hopsElem.GetRawText();
      var hopsTypeInfo = _jsonOptions.GetTypeInfo(typeof(List<MessageHop>));
      if (hopsTypeInfo != null) {
        hops = JsonSerializer.Deserialize(hopsJson, hopsTypeInfo) as List<MessageHop> ?? [];
      }
    }

    // Deserialize message payload as JsonElement (type-agnostic)
    using var eventDataDoc = JsonDocument.Parse(outboxMessage.MessageData);
    var eventPayload = eventDataDoc.RootElement.Clone();

    // Reconstruct envelope with original message ID and hops
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(outboxMessage.MessageId),
      Payload = eventPayload,
      Hops = hops
    };

    // Add hop indicating message is being published from outbox
    // Include PayloadType in metadata so consumer can deserialize correctly
    var publishHop = new MessageHop {
      Type = HopType.Current,
      ServiceName = _instanceProvider.ServiceName,
      ServiceInstanceId = _instanceProvider.InstanceId,
      Topic = outboxMessage.Destination,
      Timestamp = DateTimeOffset.UtcNow,
      Metadata = new Dictionary<string, object> {
        ["PayloadType"] = outboxMessage.MessageType  // Store string directly - will be serialized as JSON string
      }
    };
    envelope.AddHop(publishHop);

    // Parse destination (format: "topic" or "topic/subscription")
    // Include Destination in metadata for CorrelationFilter support
    var destination = new TransportDestination(
      outboxMessage.Destination,
      null,  // No routing key
      new Dictionary<string, object> {
        ["Destination"] = outboxMessage.Destination  // Used by CorrelationFilter in inbox pattern
      }
    );

    // Publish to transport
    await _transport.PublishAsync(envelope, destination, cancellationToken);
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
