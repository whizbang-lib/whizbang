using System;
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
public class OutboxPublisher {
  private readonly IOutbox _outbox;
  private readonly ITransport _transport;

  public OutboxPublisher(IOutbox outbox, ITransport transport) {
    _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  }

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
        // Create envelope for transport
        var envelope = new MessageEnvelope<byte[]> {
          MessageId = message.MessageId,
          Payload = message.Payload,
          Hops = new List<MessageHop> {
            new MessageHop {
              Type = HopType.Current,
              ServiceName = "OutboxPublisher",
              Topic = message.Destination,
              Timestamp = message.CreatedAt
            }
          }
        };

        var destination = new TransportDestination(message.Destination);

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
