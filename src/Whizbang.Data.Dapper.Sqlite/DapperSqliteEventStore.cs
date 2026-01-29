using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core;
using Whizbang.Core.Data;
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
public class DapperSqliteEventStore(
  IDbConnectionFactory connectionFactory,
  IDbExecutor executor,
  JsonSerializerOptions jsonOptions,
  IPolicyEngine policyEngine) : DapperEventStoreBase(connectionFactory, executor, jsonOptions) {

  // Unused parameter retained for backward compatibility
  private readonly IPolicyEngine _ = policyEngine;

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
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
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
    var hopsTypeInfo = JsonOptions.GetTypeInfo(typeof(List<MessageHop>));

    foreach (var row in rows) {
      // Parse JSON once to extract MessageId and compare
      using var doc = JsonDocument.Parse(row.Envelope);
      var root = doc.RootElement;

      // Try to deserialize the payload with each event type
      foreach (var eventType in eventTypes) {
        // Try to deserialize using the type's JsonTypeInfo
        var typeInfo = JsonOptions.GetTypeInfo(eventType);
        // Get the MessageId first to filter
        if (typeInfo != null && root.TryGetProperty("MessageId", out var messageIdProp)) {
          var messageIdGuid = messageIdProp.GetProperty("Value").GetGuid();

          // Filter by fromEventId
          if (fromEventId != null && messageIdGuid.CompareTo(fromEventId.Value) <= 0) {
            break; // Skip this row
          }

          // Try to deserialize the whole envelope
          // For SQLite, we need to check if Payload can deserialize as this event type
          if (root.TryGetProperty("Payload", out var payloadProp)) {
            var payloadJson = payloadProp.GetRawText();
            var payload = JsonSerializer.Deserialize(payloadJson, typeInfo);
            if (payload is IEvent eventPayload) {
              // Successfully deserialized, extract full envelope
              var messageIdTypeInfo = JsonOptions.GetTypeInfo(typeof(MessageId));
              var messageIdJson = messageIdProp.GetRawText();
              var messageIdValue = messageIdTypeInfo != null
                ? (MessageId?)JsonSerializer.Deserialize(messageIdJson, messageIdTypeInfo)
                : null;

              if (messageIdValue == null) {
                continue; // Skip if MessageId deserialization failed
              }

              var hops = root.TryGetProperty("Hops", out var hopsProp) && hopsTypeInfo != null
                ? (List<MessageHop>?)JsonSerializer.Deserialize(hopsProp.GetRawText(), hopsTypeInfo)
                : null;

              results.Add(new MessageEnvelope<IEvent> {
                MessageId = messageIdValue.Value,
                Payload = eventPayload,
                Hops = hops ?? []
              });
              break; // Found correct type, move to next row
            }
          }
        }
      }
    }

    foreach (var result in results) {
      yield return result;
    }
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
