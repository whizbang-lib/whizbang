using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IEventStore implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
public abstract class DapperEventStoreBase : IEventStore {
  protected readonly IDbConnectionFactory _connectionFactory;
  protected readonly IDbExecutor _executor;
  protected readonly JsonSerializerContext _jsonContext;

  protected DapperEventStoreBase(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    JsonSerializerContext jsonContext
  ) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);
    ArgumentNullException.ThrowIfNull(jsonContext);

    _connectionFactory = connectionFactory;
    _executor = executor;
    _jsonContext = jsonContext;
  }

  protected IDbConnectionFactory ConnectionFactory => _connectionFactory;
  protected IDbExecutor Executor => _executor;

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
  protected abstract string GetAppendSql();

  /// <summary>
  /// Gets the SQL query to read events from a stream.
  /// Should return: EventData, Metadata, Scope (all as JSONB/string)
  /// Parameters: @StreamId (Guid), @FromSequence (long)
  /// </summary>
  protected abstract string GetReadSql();

  /// <summary>
  /// Gets the SQL query to get the last sequence number for a stream.
  /// Should return MAX(sequence_number) or -1 if stream doesn't exist.
  /// Parameters: @StreamId (Guid)
  /// </summary>
  protected abstract string GetLastSequenceSql();

  /// <summary>
  /// AOT-compatible append with explicit stream ID.
  /// </summary>
  public abstract Task AppendAsync(Guid streamId, IMessageEnvelope envelope, CancellationToken cancellationToken = default);

  public abstract IAsyncEnumerable<IMessageEnvelope> ReadAsync(
    Guid streamId,
    long fromSequence,
    CancellationToken cancellationToken = default);

  public async Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetLastSequenceSql();

    var lastSequence = await _executor.ExecuteScalarAsync<long?>(
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
    public string EventData { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
    public string? Scope { get; set; }
  }
}
