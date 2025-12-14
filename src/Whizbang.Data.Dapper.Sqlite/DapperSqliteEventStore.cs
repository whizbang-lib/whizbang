using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
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
  private readonly IPolicyEngine _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));

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
        var typeInfo = _jsonOptions.GetTypeInfo(envelopeType) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
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
      } catch (Exception ex) when (IsUniqueConstraintViolation(ex)) {
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
      var typeInfo = _jsonOptions.GetTypeInfo(envelopeType) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
      if (JsonSerializer.Deserialize(row.Envelope, typeInfo) is MessageEnvelope<TMessage> envelope) {
        yield return envelope;
      }
    }
  }

  private static bool IsUniqueConstraintViolation(Exception ex) {
    // Check for SQLite UNIQUE constraint violation
    // Error code 19 = SQLITE_CONSTRAINT
    if (ex is Microsoft.Data.Sqlite.SqliteException sqliteEx) {
      return sqliteEx.SqliteErrorCode == 19;
    }
    return ex.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("Error 19", StringComparison.OrdinalIgnoreCase);
  }

  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  protected override string GetAppendSql() => @"
    INSERT INTO whizbang_event_store (stream_id, sequence_number, envelope, created_at)
    VALUES (@StreamId, @SequenceNumber, @Envelope, @CreatedAt)";

  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  protected override string GetReadSql() => @"
    SELECT envelope AS Envelope
    FROM whizbang_event_store
    WHERE stream_id = @StreamId AND sequence_number >= @FromSequence
    ORDER BY sequence_number";

  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  protected override string GetLastSequenceSql() => @"
    SELECT COALESCE(MAX(sequence_number), -1)
    FROM whizbang_event_store
    WHERE stream_id = @StreamId";

  /// <summary>
  /// Internal row structure for Dapper mapping.
  /// </summary>
  private class EnvelopeRow {
    public string Envelope { get; set; } = string.Empty;
  }
}
