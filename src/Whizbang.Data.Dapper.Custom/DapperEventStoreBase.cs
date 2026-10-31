// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IEventStore implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs</tests>
public abstract class DapperEventStoreBase : IEventStore {
  protected IDbConnectionFactory ConnectionFactory { get; }
  protected IDbExecutor Executor { get; }
  protected JsonSerializerOptions JsonOptions { get; }

  protected DapperEventStoreBase(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    JsonSerializerOptions jsonOptions
  ) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);
    ArgumentNullException.ThrowIfNull(jsonOptions);

    ConnectionFactory = connectionFactory;
    Executor = executor;
    JsonOptions = jsonOptions;
  }

  /// <summary>
  /// Ensures the connection is open. Handles both pre-opened and closed connections.
  /// </summary>
  protected static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }

  /// <summary>
  /// Gets the SQL command to append a new event to a stream.
  /// Parameters: @EventId (Guid), @StreamId (Guid), @SequenceNumber (long),
  ///             @EventType (string), @EventData (string), @Metadata (string),
  ///             @Scope (string), @CreatedAt (DateTimeOffset)
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_WithNullEnvelope_ShouldThrowAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  protected abstract string GetAppendSql();

  /// <summary>
  /// Gets the SQL query to read events from a stream.
  /// Should return: EventData, Metadata, Scope (all as JSONB/string)
  /// Parameters: @StreamId (Guid), @FromSequence (long)
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  protected abstract string GetReadSql();

  /// <summary>
  /// Gets the SQL query to get the last version number for a stream.
  /// Should return MAX(version) or -1 if stream doesn't exist.
  /// Parameters: @StreamId (Guid)
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  protected abstract string GetLastSequenceSql();

  /// <summary>
  /// AOT-compatible append with explicit stream ID.
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_WithNullEnvelope_ShouldThrowAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  public abstract Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default);

  /// <summary>
  /// Appends an event to the specified stream using a raw message.
  /// Derived classes must implement this to handle envelope lookup from IEnvelopeRegistry
  /// or create a minimal envelope if not found.
  /// </summary>
  public abstract Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull;

  /// <summary>
  /// Reads events from a stream starting from a specific sequence number.
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  public abstract IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    long fromSequence,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads events from a stream starting after a specific event ID (UUIDv7-based).
  /// </summary>
  public abstract IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    Guid? fromEventId,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads events from a stream polymorphically, deserializing each event to its concrete type.
  /// </summary>
  public abstract IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
    Guid streamId,
    Guid? fromEventId,
    IReadOnlyList<Type> eventTypes,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Perspective row retention, the resurrection-on-wake history probe. Declared here (virtual,
  /// interface default) so a derived store's override takes part in the interface mapping: a
  /// method added on the derived class alone would never be reached through
  /// <see cref="IEventStore"/>, and the interface default (false) would silently disable
  /// resurrection.
  /// </summary>
  /// <docs>fundamentals/perspectives/row-retention</docs>
  public virtual Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, CancellationToken cancellationToken = default) =>
    Task.FromResult(false);

  /// <summary>The perspective-aware form of the probe (issue #696); see the untyped overload.</summary>
  /// <docs>fundamentals/perspectives/row-retention</docs>
  public virtual Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
    Task.FromResult(false);

  /// <summary>
  /// Database-specific SQL for querying events between two checkpoint IDs (exclusive start, inclusive end).
  /// </summary>
  protected abstract string GetEventsBetweenSql();

  /// <inheritdoc />
  public async Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      CancellationToken cancellationToken = default) {

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetEventsBetweenSql();
    var parameters = new {
      StreamId = streamId,
      AfterEventId = afterEventId,
      UpToEventId = upToEventId
    };

    var rows = await Executor.QueryAsync<EventRow>(connection, sql, parameters, cancellationToken: cancellationToken);
    var envelopes = new List<MessageEnvelope<TMessage>>();

    var eventTypeInfo = JsonOptions.GetTypeInfo(typeof(TMessage));

    foreach (var row in rows) {
      var eventData = JsonSerializer.Deserialize(row.EventData, eventTypeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event of type {row.EventType}");

      envelopes.Add(_buildEnvelope<TMessage>((TMessage)eventData, row));
    }

    return envelopes;
  }

  /// <inheritdoc />
  public async Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(eventTypes);

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetEventsBetweenSql();
    var parameters = new {
      StreamId = streamId,
      AfterEventId = afterEventId,
      UpToEventId = upToEventId
    };

    var rows = await Executor.QueryAsync<EventRow>(connection, sql, parameters, cancellationToken: cancellationToken);
    var envelopes = new List<MessageEnvelope<IEvent>>();

    // Build type lookup dictionary for fast O(1) lookups (AOT-compatible)
    var typeLookup = EventTypeMatchingHelper.BuildTypeLookup(eventTypes);

    foreach (var row in rows) {
      var envelope = _tryDeserializePolymorphicRow(row, typeLookup);
      if (envelope != null) {
        envelopes.Add(envelope);
      }
    }

    return envelopes;
  }

  /// <summary>
  /// Gets the last sequence number for a stream. Returns -1 if stream doesn't exist.
  /// </summary>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  public async Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) {
    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetLastSequenceSql();

    var lastSequence = await Executor.ExecuteScalarAsync<long?>(
      connection,
      sql,
      new { StreamId = streamId },
      cancellationToken: cancellationToken);

    return lastSequence ?? -1;
  }

  /// <summary>
  /// Attempts to deserialize a single row into a polymorphic MessageEnvelope.
  /// Returns null if the event type is not in the type lookup (not relevant to this perspective).
  /// </summary>
  private MessageEnvelope<IEvent>? _tryDeserializePolymorphicRow(
      EventRow row,
      Dictionary<string, Type> typeMap) {
    // Skip events that aren't in the perspective's list (canonical resolver).
    if (!EventTypeMatchingHelper.TryResolveType(typeMap, row.EventType, out var concreteType)) {
      return null;
    }

    var eventTypeInfo = JsonOptions.GetTypeInfo(concreteType);
    var eventData = JsonSerializer.Deserialize(row.EventData, eventTypeInfo)
      ?? throw new InvalidOperationException($"Failed to deserialize event of type {row.EventType}");

    return _buildEnvelope<IEvent>((IEvent)eventData, row);
  }

  /// <summary>
  /// Builds a MessageEnvelope from deserialized event data and the raw row metadata/scope.
  /// </summary>
  private MessageEnvelope<TMessage> _buildEnvelope<TMessage>(TMessage payload, EventRow row) {
    var (messageId, hops, dispatchContext) = _deserializeMetadataAndHops(row.Metadata, row.EventType);
    StoredScope.RestoreInto(hops, StoredScope.Read(row.Scope, JsonOptions));

    return new MessageEnvelope<TMessage> {
      MessageId = MessageId.From(messageId),
      Payload = payload,
      Hops = hops,
      DispatchContext = dispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
      // #1738: the stream version, when the read selects it, which is how the archive seam sees a gap at a stream's start.
      LocalStreamVersion = row.Version,
    };
  }

  private (Guid messageId, List<MessageHop> hops, MessageDispatchContext? dispatchContext) _deserializeMetadataAndHops(string metadataJson, string eventType) {
    var metadataDictTypeInfo = JsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>));
    var metadataDict = JsonSerializer.Deserialize(metadataJson, metadataDictTypeInfo) as Dictionary<string, JsonElement>
                       ?? throw new InvalidOperationException($"Failed to deserialize metadata for event type {eventType}");

    var messageId = metadataDict.TryGetValue("message_id", out var msgIdElem)
      ? Guid.Parse(msgIdElem.GetString()!)
      : throw new InvalidOperationException("message_id not found in metadata");

    List<MessageHop> hops;
    if (metadataDict.TryGetValue("hops", out var hopsElem)) {
      var hopsTypeInfo = JsonOptions.GetTypeInfo(typeof(List<MessageHop>));
      hops = JsonSerializer.Deserialize(hopsElem.GetRawText(), hopsTypeInfo) as List<MessageHop> ?? [];
    } else {
      hops = [];
    }

    MessageDispatchContext? dispatchContext = null;
    if (metadataDict.TryGetValue("dc", out var dcElem)) {
      var dcTypeInfo = JsonOptions.GetTypeInfo(typeof(MessageDispatchContext));
      dispatchContext = JsonSerializer.Deserialize(dcElem.GetRawText(), dcTypeInfo) as MessageDispatchContext;
    }

    return (messageId, hops, dispatchContext);
  }

  /// <summary>
  /// Internal row structure for Dapper mapping (3-column JSONB pattern).
  /// </summary>
  protected class EventRow {
    public string EventType { get; set; } = string.Empty;
    public string EventData { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
    public string? Scope { get; set; }
    /// <summary>The row's position in its stream (<c>wh_event_store.version</c>), when the read selects it.</summary>
    public long? Version { get; set; }
    /// <summary>
    /// Whether the row is the first in its stream, when the read selects it (#1878): version 0, or version 1 when the
    /// stream holds no version 0.
    /// </summary>
    public bool IsFirstInStream { get; set; }
  }

  /// <inheritdoc />
  public virtual List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
    return [];
  }
}
