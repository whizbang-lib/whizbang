using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background worker that polls the outbox and publishes pending messages to the transport.
/// Implements the transactional outbox pattern for reliable message delivery.
/// Uses IServiceScopeFactory to resolve scoped IOutbox within each work cycle.
/// </summary>
public class OutboxPublisherWorker(
  IServiceScopeFactory scopeFactory,
  ITransport transport,
  JsonSerializerOptions jsonOptions,
  OutboxPublisherOptions? options = null,
  ILogger<OutboxPublisherWorker>? logger = null
  ) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly ILogger<OutboxPublisherWorker> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxPublisherWorker>.Instance;
  private readonly OutboxPublisherOptions _options = options ?? new OutboxPublisherOptions();
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation(
      "Outbox publisher worker starting (batch size: {BatchSize}, interval: {Interval}ms)",
      _options.BatchSize,
      _options.PollingIntervalMilliseconds
    );

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await ProcessPendingMessagesAsync(stoppingToken);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        _logger.LogError(ex, "Error processing outbox messages");
      }

      // Wait before next poll (unless cancelled)
      try {
        await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
      } catch (OperationCanceledException) {
        // Graceful shutdown
        break;
      }
    }

    _logger.LogInformation("Outbox publisher worker stopping");
  }

  private async Task ProcessPendingMessagesAsync(CancellationToken cancellationToken) {
    // Create a scope to resolve scoped IOutbox
    using var scope = _scopeFactory.CreateScope();
    var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

    // Get batch of pending messages
    _logger.LogInformation("OutboxPublisherWorker: Checking for pending messages...");
    var pendingMessages = await outbox.GetPendingAsync(_options.BatchSize, cancellationToken);

    _logger.LogInformation("OutboxPublisherWorker: Found {Count} pending messages", pendingMessages.Count);

    if (pendingMessages.Count == 0) {
      _logger.LogTrace("No pending outbox messages");
      return;
    }

    _logger.LogDebug("Processing {Count} pending outbox messages", pendingMessages.Count);

    var successCount = 0;
    var failureCount = 0;

    foreach (var outboxMessage in pendingMessages) {
      if (cancellationToken.IsCancellationRequested) {
        break;
      }

      try {
        await PublishMessageAsync(outboxMessage, cancellationToken);
        await outbox.MarkPublishedAsync(outboxMessage.MessageId, cancellationToken);
        successCount++;

        _logger.LogDebug(
          "Published outbox message {MessageId} to {Destination}",
          outboxMessage.MessageId,
          outboxMessage.Destination
        );
      } catch (Exception ex) {
        failureCount++;
        _logger.LogError(
          ex,
          "Failed to publish outbox message {MessageId} to {Destination}",
          outboxMessage.MessageId,
          outboxMessage.Destination
        );

        // Message remains in outbox for retry on next poll
      }
    }

    if (successCount > 0 || failureCount > 0) {
      _logger.LogInformation(
        "Outbox batch complete: {SuccessCount} published, {FailureCount} failed",
        successCount,
        failureCount
      );
    }
  }

  private async Task PublishMessageAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken) {
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
      MessageId = outboxMessage.MessageId,
      Payload = eventPayload,
      Hops = hops
    };

    // Add hop indicating message is being published from outbox
    // Include PayloadType in metadata so consumer can deserialize correctly
    var publishHop = new MessageHop {
      Type = HopType.Current,
      ServiceName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "OutboxPublisher",
      Topic = outboxMessage.Destination,
      Timestamp = DateTimeOffset.UtcNow,
      Metadata = new Dictionary<string, object> {
        ["PayloadType"] = outboxMessage.MessageType  // Store string directly - will be serialized as JSON string
      }
    };
    envelope.AddHop(publishHop);

    // Parse destination (format: "topic" or "topic/subscription")
    // Include Destination in metadata for SQL filter support
    var destination = new TransportDestination(
      outboxMessage.Destination,
      null,  // No routing key
      new Dictionary<string, object> {
        ["Destination"] = outboxMessage.Destination  // Used by SQL filters in inbox pattern
      }
    );

    // Publish to transport
    await _transport.PublishAsync(envelope, destination, cancellationToken);
  }
}

/// <summary>
/// Configuration options for the outbox publisher worker.
/// </summary>
public class OutboxPublisherOptions {
  /// <summary>
  /// Number of messages to process in each batch.
  /// Default: 100
  /// </summary>
  public int BatchSize { get; set; } = 100;

  /// <summary>
  /// Milliseconds to wait between polling for pending messages.
  /// Default: 1000 (1 second)
  /// </summary>
  public int PollingIntervalMilliseconds { get; set; } = 1000;
}
