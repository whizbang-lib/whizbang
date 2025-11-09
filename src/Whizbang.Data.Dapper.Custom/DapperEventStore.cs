using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Dapper-based implementation of IEventStore for append-only event storage.
/// Enables streaming/replay capability on transports that don't natively support it.
/// </summary>
public class DapperEventStore : IEventStore {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IDbExecutor _executor;
  private readonly JsonSerializerOptions _jsonOptions;

  public DapperEventStore(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
    _jsonOptions = new JsonSerializerOptions {
      PropertyNameCaseInsensitive = true
    };
  }

  public async Task AppendAsync(string streamKey, IMessageEnvelope envelope, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamKey);
    ArgumentNullException.ThrowIfNull(envelope);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    // Get next sequence number
    var lastSequence = await GetLastSequenceAsync(streamKey, cancellationToken);
    var nextSequence = lastSequence + 1;

    var json = JsonSerializer.Serialize(envelope, _jsonOptions);

    const string sql = @"
      INSERT INTO whizbang_event_store (stream_key, sequence_number, envelope, created_at)
      VALUES (@StreamKey, @SequenceNumber, @Envelope, @CreatedAt)";

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

    const string sql = @"
      SELECT envelope AS Envelope
      FROM whizbang_event_store
      WHERE stream_key = @StreamKey AND sequence_number >= @FromSequence
      ORDER BY sequence_number";

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

    const string sql = @"
      SELECT COALESCE(MAX(sequence_number), -1)
      FROM whizbang_event_store
      WHERE stream_key = @StreamKey";

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
  private class EventRow {
    public string Envelope { get; set; } = string.Empty;
  }
}
