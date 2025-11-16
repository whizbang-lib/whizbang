using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Append-only event store for replay/streaming scenarios.
/// Stream ID is inferred from event's [AggregateId] property using PolicyContext.GetAggregateId().
/// Uses ISequenceProvider internally for monotonic sequence numbers per stream.
/// Separate from ITraceStore (which is for observability, not event sourcing).
/// Enables streaming capability on RabbitMQ and Service Bus.
/// </summary>
public interface IEventStore {
  /// <summary>
  /// Appends an event to the specified stream (AOT-compatible).
  /// Stream ID is provided explicitly, avoiding reflection.
  /// Events are ordered by sequence number within each stream.
  /// </summary>
  /// <param name="streamId">The stream identifier (aggregate ID)</param>
  /// <param name="envelope">The message envelope to append</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the event is appended</returns>
  Task AppendAsync(Guid streamId, IMessageEnvelope envelope, CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads events from a stream by stream ID (UUID) with strong typing.
  /// Stream ID corresponds to the aggregate ID from events' [AggregateId] properties.
  /// Supports streaming and replay scenarios.
  /// This generic version provides type-safe deserialization for AOT compatibility.
  /// </summary>
  /// <typeparam name="TMessage">The message type to deserialize (must match stored event types)</typeparam>
  /// <param name="streamId">The stream identifier (aggregate ID as UUID)</param>
  /// <param name="fromSequence">The sequence number to start reading from (inclusive)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Async enumerable of strongly-typed message envelopes in sequence order</returns>
  IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets the last (highest) sequence number for a stream.
  /// Returns -1 if the stream doesn't exist or is empty.
  /// </summary>
  /// <param name="streamId">The stream identifier (aggregate ID as UUID)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The last sequence number, or -1 if empty</returns>
  Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default);
}
