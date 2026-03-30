using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of IEventStore using Dapper.
/// Stream ID is inferred from event's [AggregateId] property.
/// Uses retry logic with UNIQUE constraint for thread-safe sequence number generation.
/// </summary>
#pragma warning disable CS9113, S1144 // Primary constructor parameter is unread - retained for backward compatibility
public class DapperSqliteEventStore(
  IDbConnectionFactory connectionFactory,
  IDbExecutor executor,
  JsonSerializerOptions jsonOptions,
  IPolicyEngine policyEngine) : DapperEventStoreBase(connectionFactory, executor, jsonOptions) {
#pragma warning restore CS9113, S1144

  /// <summary>
  /// Appends an event to the specified stream (AOT-compatible).
  /// Stream ID is provided explicitly, avoiding reflection.
  /// Uses retry logic with the UNIQUE constraint to handle concurrent writes.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_WithNullEnvelope_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  public override async Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);

    const int maxRetries = 10;
    var lastException = default(Exception);

    for (int attempt = 0; attempt < maxRetries; attempt++) {
      try {
        using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
        connection.Open();

        // Get next sequence number
        var lastSequence = await GetLastSequenceAsync(streamId, cancellationToken);
        var nextSequence = lastSequence + 1;

        // Serialize envelope (AOT-compatible via WhizbangJsonContext in resolver chain)
        var envelopeType = typeof(MessageEnvelope<TMessage>);
        var typeInfo = JsonOptions.GetTypeInfo(envelopeType) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
        var json = JsonSerializer.Serialize(envelope, typeInfo);

        // Try to insert with sequence number
        await Executor.ExecuteAsync(
          connection,
          GetAppendSql(),
          new {
            StreamId = streamId,
            SequenceNumber = nextSequence,
            Envelope = json,
            CreatedAt = DateTimeOffset.UtcNow
          },
          cancellationToken: cancellationToken);

        // Success - exit retry loop
        return;
      } catch (Exception ex) when (_isUniqueConstraintViolation(ex)) {
        // UNIQUE constraint violation - another thread inserted the same sequence
        // Retry with next sequence number
        lastException = ex;
        await Task.Delay(10 * (attempt + 1), cancellationToken); // Exponential backoff
      }
    }

    // Max retries exceeded
    throw new InvalidOperationException(
      $"Failed to append event to stream '{streamId}' after {maxRetries} attempts due to concurrent writes.",
      lastException);
  }

  /// <inheritdoc />
  public override Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);

    // Create a minimal envelope - registry-based lookup would require constructor injection
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = System.Diagnostics.Activity.Current?.Id
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    return AppendAsync(streamId, envelope, cancellationToken);
  }

  /// <summary>
  /// Reads events from a stream by stream ID (UUID).
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  public override async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    long fromSequence,
    [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var rows = await Executor.QueryAsync<EnvelopeRow>(
      connection,
      GetReadSql(),
      new {
        StreamId = streamId,
        FromSequence = fromSequence
      },
      cancellationToken: cancellationToken);

    foreach (var row in rows) {
      // Deserialize with concrete message type (AOT-compatible)
      var envelopeType = typeof(MessageEnvelope<TMessage>);
      var typeInfo = JsonOptions.GetTypeInfo(envelopeType) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
      if (JsonSerializer.Deserialize(row.Envelope, typeInfo) is MessageEnvelope<TMessage> envelope) {
        yield return envelope;
      }
    }
  }

  /// <summary>
  /// Reads events from a stream starting after a specific event ID (UUIDv7-based).
  /// Uses UUIDv7 time ordering for natural event sequence without sequence numbers.
  /// </summary>
  public override async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    Guid? fromEventId,
    [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    // NOTE: SQLite doesn't have built-in UUID comparison, so we filter in C# after deserialization
    const string sql = @"SELECT envelope AS Envelope
          FROM whizbang_event_store
          WHERE stream_id = @StreamId
          ORDER BY created_at, rowid";

    var rows = await Executor.QueryAsync<EnvelopeRow>(
      connection,
      sql,
      new {
        StreamId = streamId,
        FromEventId = fromEventId
      },
      cancellationToken: cancellationToken);

    foreach (var row in rows) {
      var envelopeType = typeof(MessageEnvelope<TMessage>);
      var typeInfo = JsonOptions.GetTypeInfo(envelopeType) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
      // If fromEventId specified, filter in C# (SQLite doesn't support UUID comparison)
      if (JsonSerializer.Deserialize(row.Envelope, typeInfo) is MessageEnvelope<TMessage> envelope &&
          (fromEventId == null || envelope.MessageId.Value.CompareTo(fromEventId.Value) > 0)) {
        yield return envelope;
      }
    }
  }

  /// <summary>
  /// Reads events from a stream polymorphically, deserializing each event to its concrete type.
  /// NOTE: SQLite stores full envelope JSON, so we deserialize the JSON directly.
  /// This implementation is not AOT-compatible and is intended for testing only.
  /// </summary>
  public override async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
    Guid streamId,
    Guid? fromEventId,
    IReadOnlyList<Type> eventTypes,
    [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    // NOTE: SQLite doesn't have built-in UUID comparison, so we filter in C# after deserialization
    const string sql = @"SELECT envelope AS Envelope
          FROM whizbang_event_store
          WHERE stream_id = @StreamId
          ORDER BY created_at, rowid";

    var rows = await Executor.QueryAsync<EnvelopeRow>(
      connection,
      sql,
      new {
        StreamId = streamId,
        FromEventId = fromEventId
      },
      cancellationToken: cancellationToken);

    // Deserialize all rows first to avoid yield in try-catch
    var results = new List<MessageEnvelope<IEvent>>();

    foreach (var row in rows) {
      var envelope = _tryDeserializeRow(row.Envelope, fromEventId, eventTypes);
      if (envelope != null) {
        results.Add(envelope);
      }
    }

    foreach (var result in results) {
      yield return result;
    }
  }

  /// <summary>
  /// Attempts to deserialize a row's envelope JSON into a polymorphic MessageEnvelope&lt;IEvent&gt;.
  /// Returns null if the row should be skipped (filtered by fromEventId or no matching type).
  /// </summary>
  private MessageEnvelope<IEvent>? _tryDeserializeRow(string envelopeJson, Guid? fromEventId, IReadOnlyList<Type> eventTypes) {
    using var doc = JsonDocument.Parse(envelopeJson);
    var root = doc.RootElement;

    if (!_tryExtractMessageId(root, out var messageIdProp, out var messageIdGuid)) {
      return null;
    }

    if (_shouldSkipByEventId(fromEventId, messageIdGuid)) {
      return null;
    }

    return _tryMatchEventType(root, eventTypes, messageIdProp);
  }

  /// <summary>
  /// Extracts the MessageId property and its Guid value from the root JSON element.
  /// </summary>
  private static bool _tryExtractMessageId(JsonElement root, out JsonElement messageIdProp, out Guid messageIdGuid) {
    messageIdGuid = Guid.Empty;
    if (!root.TryGetProperty("MessageId", out messageIdProp)) {
      return false;
    }

    messageIdGuid = messageIdProp.GetProperty("Value").GetGuid();
    return true;
  }

  /// <summary>
  /// Returns true if this row should be skipped based on the fromEventId filter.
  /// </summary>
  private static bool _shouldSkipByEventId(Guid? fromEventId, Guid messageIdGuid) =>
    fromEventId != null && messageIdGuid.CompareTo(fromEventId.Value) <= 0;

  /// <summary>
  /// Iterates event types and returns the first successful deserialization as a polymorphic envelope.
  /// </summary>
  private MessageEnvelope<IEvent>? _tryMatchEventType(JsonElement root, IReadOnlyList<Type> eventTypes, JsonElement messageIdProp) {
    foreach (var eventType in eventTypes) {
      var typeInfo = JsonOptions.GetTypeInfo(eventType);
      if (typeInfo == null) {
        continue;
      }

      var envelope = _tryDeserializeAsEventType(root, typeInfo, messageIdProp);
      if (envelope != null) {
        return envelope;
      }
    }

    return null;
  }

  /// <summary>
  /// Tries to deserialize the payload as the given event type and reconstruct the envelope.
  /// Returns null if the payload doesn't match the event type.
  /// </summary>
  private MessageEnvelope<IEvent>? _tryDeserializeAsEventType(
    JsonElement root,
    System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo,
    JsonElement messageIdProp) {

    var eventPayload = _tryDeserializePayload(root, typeInfo);
    if (eventPayload == null) {
      return null;
    }

    var messageId = _tryDeserializeMessageId(messageIdProp);
    if (messageId == null) {
      return null;
    }

    return new MessageEnvelope<IEvent> {
      MessageId = messageId.Value,
      Payload = eventPayload,
      Hops = _deserializeHops(root),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
    };
  }

  /// <summary>
  /// Deserializes the Payload property as the given event type. Returns null if not an IEvent.
  /// </summary>
  private static IEvent? _tryDeserializePayload(
    JsonElement root,
    System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo) {

    if (!root.TryGetProperty("Payload", out var payloadProp)) {
      return null;
    }

    return JsonSerializer.Deserialize(payloadProp.GetRawText(), typeInfo) as IEvent;
  }

  /// <summary>
  /// Deserializes the MessageId from its JSON element. Returns null on failure.
  /// </summary>
  private MessageId? _tryDeserializeMessageId(JsonElement messageIdProp) {
    var messageIdTypeInfo = JsonOptions.GetTypeInfo(typeof(MessageId));
    if (messageIdTypeInfo == null) {
      return null;
    }

    return JsonSerializer.Deserialize(messageIdProp.GetRawText(), messageIdTypeInfo) as MessageId?;
  }

  /// <summary>
  /// Deserializes the Hops array from the root element, returning an empty list if absent.
  /// </summary>
  private List<MessageHop> _deserializeHops(JsonElement root) {
    if (!root.TryGetProperty("Hops", out var hopsProp)) {
      return [];
    }

    var hopsTypeInfo = JsonOptions.GetTypeInfo(typeof(List<MessageHop>));
    if (hopsTypeInfo == null) {
      return [];
    }

    return JsonSerializer.Deserialize(hopsProp.GetRawText(), hopsTypeInfo) as List<MessageHop> ?? [];
  }

  private static bool _isUniqueConstraintViolation(Exception ex) {
    // Check for SQLite UNIQUE constraint violation
    // Error code 19 = SQLITE_CONSTRAINT
    if (ex is Microsoft.Data.Sqlite.SqliteException sqliteEx) {
      return sqliteEx.SqliteErrorCode == 19;
    }
    return ex.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("Error 19", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Returns the SQLite-specific SQL for appending an event to the event store.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  protected override string GetAppendSql() => @"
    INSERT INTO whizbang_event_store (stream_id, sequence_number, envelope, created_at)
    VALUES (@StreamId, @SequenceNumber, @Envelope, @CreatedAt)";

  /// <summary>
  /// Returns the SQLite-specific SQL for reading events from a stream by sequence number.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  protected override string GetReadSql() => @"
    SELECT envelope AS Envelope
    FROM whizbang_event_store
    WHERE stream_id = @StreamId AND sequence_number >= @FromSequence
    ORDER BY sequence_number";

  /// <summary>
  /// Returns the SQLite-specific SQL for retrieving the last sequence number in a stream.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  protected override string GetLastSequenceSql() => @"
    SELECT COALESCE(MAX(sequence_number), -1)
    FROM whizbang_event_store
    WHERE stream_id = @StreamId";

  protected override string GetEventsBetweenSql() {
    throw new NotImplementedException();
  }

  /// <summary>
  /// Internal row structure for Dapper mapping.
  /// </summary>
  private sealed class EnvelopeRow {
    public string Envelope { get; set; } = string.Empty;
  }
}
