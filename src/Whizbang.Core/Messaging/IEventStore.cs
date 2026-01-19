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
  /// Gets events between two checkpoint positions (exclusive start, inclusive end).
  /// Used by lifecycle receptors to load events that were just processed by a perspective.
  /// Events are ordered by UUID v7 (time-ordered).
  /// </summary>
  /// <typeparam name="TMessage">The message type to deserialize (must match stored event types)</typeparam>
  /// <param name="streamId">The stream identifier (aggregate ID as UUID)</param>
  /// <param name="afterEventId">The event ID to start reading after (exclusive). Null means start from beginning.</param>
  /// <param name="upToEventId">The event ID to read up to (inclusive).</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of message envelopes between the two checkpoints, ordered by event ID (UUID v7)</returns>
  /// <remarks>
  /// This method is primarily used by lifecycle receptors at PostPerspective stages to load
  /// the events that were just processed. The PerspectiveWorker tracks lastProcessedEventId
  /// (before) and result.LastEventId (after), then calls this method to get the events in between.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_WithEventsInRange_ReturnsEventsBetweenCheckpointsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_NullAfterEventId_ReturnsFromStartAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_NoEventsInRange_ReturnsEmptyListAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_MultipleEvents_ReturnsInUuidV7OrderAsync</tests>
  /// <docs>core-concepts/lifecycle-receptors</docs>
  Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets events between two checkpoint positions, deserializing each event to its concrete type.
  /// Uses the EventType column to determine which concrete type to deserialize to.
  /// This is the polymorphic version of GetEventsBetweenAsync for perspectives that handle multiple event types.
  /// Used by lifecycle receptors to load events that were just processed by a perspective.
  /// </summary>
  /// <param name="streamId">The stream identifier (aggregate ID as UUID)</param>
  /// <param name="afterEventId">The event ID to start reading after (exclusive). Null means start from beginning.</param>
  /// <param name="upToEventId">The event ID to read up to (inclusive).</param>
  /// <param name="eventTypes">The concrete event types to deserialize (must be registered in JsonSerializerContext)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of message envelopes with polymorphic payloads between the two checkpoints, ordered by event ID (UUID v7)</returns>
  /// <remarks>
  /// <para>
  /// This method is primarily used by lifecycle receptors at PostPerspective stages when a perspective
  /// handles multiple event types. The PerspectiveWorker needs to load the actual events to invoke
  /// lifecycle receptors with concrete event payloads.
  /// </para>
  /// <para>
  /// Unlike GetEventsBetweenAsync&lt;TMessage&gt;, this method can handle mixed event types by looking
  /// up the EventType column and deserializing to the appropriate concrete type. All event types must
  /// be provided in the eventTypes parameter for AOT compatibility.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_WithMixedEventTypes_ReturnsAllEventsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_NullAfterEventId_ReturnsFromStartAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_NoEventsInRange_ReturnsEmptyListAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_UnknownEventType_ThrowsInvalidOperationExceptionAsync</tests>
  /// <docs>core-concepts/lifecycle-receptors</docs>
  Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default);

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
