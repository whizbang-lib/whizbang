using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Publishes pending outbox messages via a transport.
/// Polls the outbox, publishes messages, and marks them as published on success.
/// Handles errors gracefully without losing messages.
/// </summary>
public class OutboxPublisher(
  IOutbox outbox,
  ITransport transport,
  System.Text.Json.JsonSerializerOptions jsonOptions,
  IServiceInstanceProvider? instanceProvider = null) {
  private readonly IOutbox _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly System.Text.Json.JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly IServiceInstanceProvider? _instanceProvider = instanceProvider;

  /// <summary>
  /// Publishes a batch of pending messages from the outbox.
  /// Messages are published one at a time, with each success marked immediately.
  /// Failures are logged but do not stop processing of other messages.
  /// </summary>
  /// <param name="batchSize">Maximum number of messages to process in this batch</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when all messages in the batch have been processed</returns>
  public async Task PublishPendingAsync(int batchSize, CancellationToken cancellationToken = default) {
    // Get pending messages
    var pending = await _outbox.GetPendingAsync(batchSize, cancellationToken);

    if (pending.Count == 0) {
      return;
    }

    // Process each message
    foreach (var message in pending) {
      if (cancellationToken.IsCancellationRequested) {
        break;
      }

      try {
        // Reconstruct envelope from JSONB columns
        // Parse metadata to extract hops
        using var metadataDoc = System.Text.Json.JsonDocument.Parse(message.Metadata);
        var metadataRoot = metadataDoc.RootElement;

        var hops = new List<MessageHop>();
        if (metadataRoot.TryGetProperty("hops", out var hopsElem)) {
          var hopsJson = hopsElem.GetRawText();
          // Use AOT-compatible deserialization with WhizbangJsonContext
          var hopsTypeInfo = _jsonOptions.GetTypeInfo(typeof(List<MessageHop>));
          if (hopsTypeInfo != null) {
            hops = System.Text.Json.JsonSerializer.Deserialize(hopsJson, hopsTypeInfo) as List<MessageHop> ?? [];
          }
        }

        // Deserialize message payload as JsonElement (type-agnostic)
        using var eventDataDoc = System.Text.Json.JsonDocument.Parse(message.MessageData);
        var eventPayload = eventDataDoc.RootElement.Clone();

        // Create envelope with original hops
        var envelope = new MessageEnvelope<System.Text.Json.JsonElement> {
          MessageId = message.MessageId,
          Payload = eventPayload,
          Hops = hops
        };

        // Add hop for this publishing action
        envelope.AddHop(new MessageHop {
          Type = HopType.Current,
          ServiceName = _instanceProvider?.ServiceName ?? "OutboxPublisher",
          ServiceInstanceId = _instanceProvider?.InstanceId ?? Guid.Empty,
          Topic = message.Destination,
          Timestamp = DateTimeOffset.UtcNow
        });

        // Include Destination in metadata for CorrelationFilter support
        var destination = new TransportDestination(
          message.Destination,
          null,  // No routing key
          new Dictionary<string, object> {
            ["Destination"] = message.Destination  // Used by CorrelationFilter in inbox pattern
          }
        );

        // Publish via transport
        await _transport.PublishAsync(envelope, destination, cancellationToken);

        // Mark as published
        await _outbox.MarkPublishedAsync(message.MessageId, cancellationToken);
      } catch (Exception) {
        // Silently continue processing other messages
        // In production, this would log the error
      }
    }
  }
}
