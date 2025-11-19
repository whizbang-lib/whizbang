using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background worker that polls the outbox and publishes pending messages to the transport.
/// Implements the transactional outbox pattern for reliable message delivery.
/// </summary>
public class OutboxPublisherWorker : BackgroundService {
  private readonly IOutbox _outbox;
  private readonly ITransport _transport;
  private readonly ILogger<OutboxPublisherWorker> _logger;
  private readonly OutboxPublisherOptions _options;

  public OutboxPublisherWorker(
    IOutbox outbox,
    ITransport transport,
    OutboxPublisherOptions? options = null,
    ILogger<OutboxPublisherWorker>? logger = null
  ) {
    _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _options = options ?? new OutboxPublisherOptions();
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxPublisherWorker>.Instance;
  }

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
    // Get batch of pending messages
    var pendingMessages = await _outbox.GetPendingAsync(_options.BatchSize, cancellationToken);

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
        await _outbox.MarkPublishedAsync(outboxMessage.MessageId, cancellationToken);
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

    // Extract hops from metadata
    var hops = new List<MessageHop>();
    if (metadataRoot.TryGetProperty("hops", out var hopsElem)) {
      var hopsJson = hopsElem.GetRawText();
      hops = JsonSerializer.Deserialize<List<MessageHop>>(hopsJson) ?? new List<MessageHop>();
    }

    // Deserialize event payload as JsonElement (type-agnostic)
    using var eventDataDoc = JsonDocument.Parse(outboxMessage.EventData);
    var eventPayload = eventDataDoc.RootElement.Clone();

    // Reconstruct envelope with original message ID and hops
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = outboxMessage.MessageId,
      Payload = eventPayload,
      Hops = hops
    };

    // Add hop indicating message is being published from outbox
    var publishHop = new MessageHop {
      Type = HopType.Current,
      ServiceName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "OutboxPublisher",
      Topic = outboxMessage.Destination,
      Timestamp = DateTimeOffset.UtcNow
    };
    envelope.AddHop(publishHop);

    // Parse destination (format: "topic" or "topic/subscription")
    var destination = new TransportDestination(outboxMessage.Destination);

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
