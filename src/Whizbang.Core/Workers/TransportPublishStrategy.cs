using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// Default implementation of IMessagePublishStrategy that publishes messages via ITransport.
/// Reconstructs the MessageEnvelope from OutboxWork and publishes to the configured transport.
/// </summary>
public class TransportPublishStrategy : IMessagePublishStrategy {
  private readonly ITransport _transport;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly ITransportReadinessCheck _readinessCheck;

  /// <summary>
  /// Creates a new TransportPublishStrategy.
  /// </summary>
  /// <param name="transport">The transport to publish messages to</param>
  /// <param name="jsonOptions">JSON serialization options for deserializing message data</param>
  /// <param name="readinessCheck">Readiness check to verify transport is ready before publishing</param>
  public TransportPublishStrategy(ITransport transport, JsonSerializerOptions jsonOptions, ITransportReadinessCheck readinessCheck) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    _readinessCheck = readinessCheck ?? throw new ArgumentNullException(nameof(readinessCheck));
  }

  /// <summary>
  /// Checks if the transport is ready to accept messages by delegating to the readiness check.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>True if transport is ready, false otherwise</returns>
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    return _readinessCheck.IsReadyAsync(cancellationToken);
  }

  /// <summary>
  /// Publishes a single outbox message to the configured transport.
  /// Reconstructs the MessageEnvelope from stored JSON data and publishes via ITransport.
  /// </summary>
  /// <param name="work">The outbox work item containing the message to publish</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Result indicating success/failure and any error details</returns>
  public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
    try {
      // Reconstruct the envelope from stored JSON data
      var envelope = ReconstructEnvelope(work);

      // Create transport destination
      var destination = new TransportDestination(work.Destination);

      // Publish to transport
      await _transport.PublishAsync(envelope, destination, cancellationToken);

      // Return success result
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      };
    } catch (Exception ex) {
      // Return failure result with error details
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = false,
        CompletedStatus = work.Status, // Already stored, publish failed
        Error = $"{ex.GetType().Name}: {ex.Message}"
      };
    }
  }

  /// <summary>
  /// Reconstructs a MessageEnvelope from OutboxWork data.
  /// Deserializes the stored JSON data and metadata to rebuild the original envelope.
  /// AOT-safe: Uses JsonSerializerOptions with registered type resolvers from JsonContextRegistry.
  /// </summary>
#pragma warning disable IL2026 // Suppressed because we use AOT-compatible JsonSerializerOptions with type resolvers
  private IMessageEnvelope ReconstructEnvelope(OutboxWork work) {
    // Deserialize metadata to get envelope properties (MessageId, Hops, etc.)
    var metadata = JsonSerializer.Deserialize<EnvelopeMetadata>(work.Metadata, _jsonOptions)
      ?? throw new InvalidOperationException($"Failed to deserialize metadata for message {work.MessageId}");

    // Deserialize the message payload as JsonElement (type-agnostic)
    var payload = JsonSerializer.Deserialize<JsonElement>(work.MessageData, _jsonOptions);

    // Create a generic envelope with JsonElement payload
    // The transport will serialize this back to JSON for transmission
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = metadata.MessageId,
      Payload = payload,
      Hops = metadata.Hops
    };

    return envelope;
  }
#pragma warning restore IL2026
}

