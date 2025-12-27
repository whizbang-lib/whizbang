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
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryEventStoreTests.cs</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStoreTests.cs</tests>
public interface IEventStore {
  /// <summary>
  /// Appends an event to the specified stream (AOT-compatible).
  /// Stream ID is provided explicitly, avoiding reflection.
  /// Events are ordered by sequence number within each stream.
  /// Generic for AOT compatibility - allows compile-time type information for JSON serialization.
  /// </summary>
  /// <typeparam name="TMessage">The message payload type (must be registered in JsonSerializerContext)</typeparam>
  /// <param name="streamId">The stream identifier (aggregate ID)</param>
  /// <param name="envelope">The message envelope to append</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the event is appended</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_WithNullEnvelope_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_WithHighConcurrency_ShouldRetryAndSucceedAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_ExtremelyHighConcurrency_ShouldHandleRetriesAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_ConcurrentAppendsToSameSequence_ShouldResolveConflictsAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_WithRetryBackoff_ShouldEventuallySucceedAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_ExtremeContention_ShouldEventuallyThrowMaxRetriesAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_WithNonUniqueViolationException_ShouldPropagateExceptionAsync</tests>
  Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default);

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
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads events from a stream by stream ID (UUID) starting after a specific event ID.
  /// Uses UUIDv7 (Medo.Uuid7) for time-based ordering - events are ordered by event ID directly.
  /// Supports perspective checkpoint processing where last processed event ID is tracked.
  /// </summary>
  /// <typeparam name="TMessage">The message type to deserialize (must match stored event types)</typeparam>
  /// <param name="streamId">The stream identifier (aggregate ID as UUID)</param>
  /// <param name="fromEventId">The event ID to start reading after (null = from beginning). Events after this ID are returned.</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Async enumerable of strongly-typed message envelopes ordered by event ID (UUIDv7)</returns>
  IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads events from a stream polymorphically, deserializing each event to its concrete type.
  /// Uses the EventType column to determine which concrete type to deserialize to.
  /// Supports perspectives that handle multiple event types.
  /// </summary>
  /// <param name="streamId">The stream identifier (aggregate ID as UUID)</param>
  /// <param name="fromEventId">The event ID to start reading after (null = from beginning)</param>
  /// <param name="eventTypes">The concrete event types to deserialize (must be registered in JsonSerializerContext)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Async enumerable of message envelopes with polymorphic payloads ordered by event ID (UUIDv7)</returns>
  IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets the last (highest) sequence number for a stream.
  /// Returns -1 if the stream doesn't exist or is empty.
  /// </summary>
  /// <param name="streamId">The stream identifier (aggregate ID as UUID)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The last sequence number, or -1 if empty</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default);
}
