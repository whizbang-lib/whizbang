using System.Data;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Dapper-based implementation of IOutbox for transactional outbox pattern.
/// Stores outgoing messages in the database for reliable delivery.
/// </summary>
public class DapperOutbox : IOutbox {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IDbExecutor _executor;

  public DapperOutbox(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
  }

  public async Task StoreAsync(MessageId messageId, string destination, byte[] payload, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(destination);
    ArgumentNullException.ThrowIfNull(payload);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    const string sql = @"
      INSERT INTO whizbang_outbox (message_id, destination, payload, created_at, published_at)
      VALUES (@MessageId, @Destination, @Payload, @CreatedAt, NULL)";

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        MessageId = messageId.Value,
        Destination = destination,
        Payload = payload,
        CreatedAt = DateTimeOffset.UtcNow
      },
      cancellationToken: cancellationToken);
  }

  public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    const string sql = @"
      SELECT message_id AS MessageId, destination AS Destination, payload AS Payload, created_at AS CreatedAt
      FROM whizbang_outbox
      WHERE published_at IS NULL
      ORDER BY created_at
      LIMIT @BatchSize";

    var rows = await _executor.QueryAsync<OutboxRow>(
      connection,
      sql,
      new { BatchSize = batchSize },
      cancellationToken: cancellationToken);

    return rows.Select(r => new OutboxMessage(
      MessageId.From(r.MessageId),
      r.Destination,
      r.Payload,
      r.CreatedAt
    )).ToList();
  }

  public async Task MarkPublishedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    const string sql = @"
      UPDATE whizbang_outbox
      SET published_at = @PublishedAt
      WHERE message_id = @MessageId";

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        MessageId = messageId.Value,
        PublishedAt = DateTimeOffset.UtcNow
      },
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Internal row structure for Dapper mapping.
  /// </summary>
  private class OutboxRow {
    public Guid MessageId { get; set; }
    public string Destination { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAt { get; set; }
  }
}
