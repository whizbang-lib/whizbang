using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IEventStore implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs</tests>
public abstract class DapperEventStoreBase : IEventStore {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IDbExecutor _executor;
  private readonly JsonSerializerOptions _jsonOptions;

  protected DapperEventStoreBase(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    JsonSerializerOptions jsonOptions
  ) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);
    ArgumentNullException.ThrowIfNull(jsonOptions);

    _connectionFactory = connectionFactory;
    _executor = executor;
    _jsonOptions = jsonOptions;
  }

  protected IDbConnectionFactory ConnectionFactory => _connectionFactory;
  protected IDbExecutor Executor => _executor;
  protected JsonSerializerOptions JsonOptions => _jsonOptions;

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
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_WithNullEnvelope_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  protected abstract string GetAppendSql();

  /// <summary>
  /// Gets the SQL query to read events from a stream.
  /// Should return: EventData, Metadata, Scope (all as JSONB/string)
  /// Parameters: @StreamId (Guid), @FromSequence (long)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  protected abstract string GetReadSql();

  /// <summary>
  /// Gets the SQL query to get the last sequence number for a stream.
  /// Should return MAX(sequence_number) or -1 if stream doesn't exist.
  /// Parameters: @StreamId (Guid)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  protected abstract string GetLastSequenceSql();

  /// <summary>
  /// AOT-compatible append with explicit stream ID.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_WithNullEnvelope_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  public abstract Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads events from a stream starting from a specific sequence number.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
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
  /// Gets the last sequence number for a stream. Returns -1 if stream doesn't exist.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
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
  /// Internal row structure for Dapper mapping (3-column JSONB pattern).
  /// </summary>
  protected class EventRow {
    public string EventType { get; set; } = string.Empty;
    public string EventData { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
    public string? Scope { get; set; }
  }
}
