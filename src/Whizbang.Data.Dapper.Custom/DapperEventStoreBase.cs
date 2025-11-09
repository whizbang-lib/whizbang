using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
  protected readonly JsonSerializerOptions _jsonOptions;

  protected DapperEventStoreBase(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
    _jsonOptions = new JsonSerializerOptions {
      PropertyNameCaseInsensitive = true,
      PropertyNamingPolicy = null, // Preserve PascalCase property names
      WriteIndented = false,
      DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never, // Include all properties
      IncludeFields = true // Include fields as well as properties
    };
  }

  /// <summary>
  /// Gets the SQL command to append a new event to a stream.
  /// Parameters: @StreamKey (string), @SequenceNumber (long), @Envelope (string), @CreatedAt (DateTimeOffset)
  /// </summary>
  protected abstract string GetAppendSql();

  /// <summary>
  /// Gets the SQL query to read events from a stream.
  /// Should return: Envelope (string)
  /// Parameters: @StreamKey (string), @FromSequence (long)
  /// </summary>
  protected abstract string GetReadSql();

  /// <summary>
  /// Gets the SQL query to get the last sequence number for a stream.
  /// Should return MAX(sequence_number) or -1 if stream doesn't exist.
  /// Parameters: @StreamKey (string)
  /// </summary>
  protected abstract string GetLastSequenceSql();

  public async Task AppendAsync(string streamKey, IMessageEnvelope envelope, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamKey);
    ArgumentNullException.ThrowIfNull(envelope);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    // Get next sequence number
    var lastSequence = await GetLastSequenceAsync(streamKey, cancellationToken);
    var nextSequence = lastSequence + 1;

    // Serialize using the actual runtime type to preserve all properties
    var json = JsonSerializer.Serialize(envelope, envelope.GetType(), _jsonOptions);
    var sql = GetAppendSql();

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        StreamKey = streamKey,
        SequenceNumber = nextSequence,
        Envelope = json,
        CreatedAt = DateTimeOffset.UtcNow
      },
      cancellationToken: cancellationToken);
  }

  public async IAsyncEnumerable<IMessageEnvelope> ReadAsync(
    string streamKey,
    long fromSequence,
    [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamKey);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    var sql = GetReadSql();

    var rows = await _executor.QueryAsync<EventRow>(
      connection,
      sql,
      new {
        StreamKey = streamKey,
        FromSequence = fromSequence
      },
      cancellationToken: cancellationToken);

    foreach (var row in rows) {
      var envelope = JsonSerializer.Deserialize<MessageEnvelope<object>>(row.Envelope, _jsonOptions);
      if (envelope != null) {
        yield return envelope;
      }
    }
  }

  public async Task<long> GetLastSequenceAsync(string streamKey, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamKey);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    var sql = GetLastSequenceSql();

    var lastSequence = await _executor.ExecuteScalarAsync<long?>(
      connection,
      sql,
      new { StreamKey = streamKey },
      cancellationToken: cancellationToken);

    return lastSequence ?? -1;
  }

  /// <summary>
  /// Internal row structure for Dapper mapping.
  /// </summary>
  protected class EventRow {
    public string Envelope { get; set; } = string.Empty;
  }
}
