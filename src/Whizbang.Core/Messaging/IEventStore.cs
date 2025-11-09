using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Append-only event store for replay/streaming scenarios.
/// Uses ISequenceProvider (existing) internally for monotonic sequence numbers.
/// Separate from ITraceStore (which is for observability, not event sourcing).
/// Enables streaming capability on RabbitMQ and Service Bus.
/// </summary>
public interface IEventStore {
  /// <summary>
  /// Appends an event to the specified stream.
  /// Events are ordered by sequence number within each stream.
  /// </summary>
  /// <param name="streamKey">The stream identifier (e.g., customer ID, order ID)</param>
  /// <param name="envelope">The message envelope to append</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the event is appended</returns>
  Task AppendAsync(string streamKey, IMessageEnvelope envelope, CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads events from a stream starting at a specific sequence number.
  /// Supports streaming and replay scenarios.
  /// </summary>
  /// <param name="streamKey">The stream identifier</param>
  /// <param name="fromSequence">The sequence number to start reading from (inclusive)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Async enumerable of message envelopes in sequence order</returns>
  IAsyncEnumerable<IMessageEnvelope> ReadAsync(string streamKey, long fromSequence, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets the last (highest) sequence number for a stream.
  /// Returns -1 if the stream doesn't exist or is empty.
  /// </summary>
  /// <param name="streamKey">The stream identifier</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The last sequence number, or -1 if empty</returns>
  Task<long> GetLastSequenceAsync(string streamKey, CancellationToken cancellationToken = default);
}
