using System.Data;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IOutbox implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
public abstract class DapperOutboxBase : IOutbox {
  protected readonly IDbConnectionFactory _connectionFactory;
  protected readonly IDbExecutor _executor;

  protected DapperOutboxBase(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
  }

  /// <summary>
  /// Gets the SQL command to store a new outbox message.
  /// Parameters: @MessageId (Guid), @Destination (string), @Payload (byte[]), @CreatedAt (DateTimeOffset)
  /// </summary>
  protected abstract string GetStoreSql();

  /// <summary>
  /// Gets the SQL query to retrieve pending outbox messages.
  /// Should return: MessageId, Destination, Payload, CreatedAt
  /// Parameters: @BatchSize (int)
  /// </summary>
  protected abstract string GetPendingSql();

  /// <summary>
  /// Gets the SQL command to mark a message as published.
  /// Parameters: @MessageId (Guid), @PublishedAt (DateTimeOffset)
  /// </summary>
  protected abstract string GetMarkPublishedSql();

  public async Task StoreAsync(MessageId messageId, string destination, byte[] payload, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(destination);
    ArgumentNullException.ThrowIfNull(payload);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    var sql = GetStoreSql();

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

    var sql = GetPendingSql();

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

    var sql = GetMarkPublishedSql();

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
  protected class OutboxRow {
    public Guid MessageId { get; set; }
    public string Destination { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAt { get; set; }
  }
}
