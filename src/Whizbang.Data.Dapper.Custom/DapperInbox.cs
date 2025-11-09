using System.Data;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Dapper-based implementation of IInbox for message deduplication.
/// Stores processed message IDs in the database to ensure exactly-once processing.
/// </summary>
public class DapperInbox : IInbox {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IDbExecutor _executor;

  public DapperInbox(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
  }

  public async Task<bool> HasProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    const string sql = @"
      SELECT COUNT(1)
      FROM whizbang_inbox
      WHERE message_id = @MessageId";

    var count = await _executor.ExecuteScalarAsync<long>(
      connection,
      sql,
      new { MessageId = messageId.Value },
      cancellationToken: cancellationToken);

    return count > 0;
  }

  public async Task MarkProcessedAsync(MessageId messageId, string handlerName, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(handlerName);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    const string sql = @"
      INSERT INTO whizbang_inbox (message_id, handler_name, processed_at)
      VALUES (@MessageId, @HandlerName, @ProcessedAt)
      ON CONFLICT (message_id) DO NOTHING";

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        MessageId = messageId.Value,
        HandlerName = handlerName,
        ProcessedAt = DateTimeOffset.UtcNow
      },
      cancellationToken: cancellationToken);
  }

  public async Task CleanupExpiredAsync(TimeSpan retention, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    const string sql = @"
      DELETE FROM whizbang_inbox
      WHERE processed_at < @CutoffDate";

    var cutoffDate = DateTimeOffset.UtcNow - retention;

    await _executor.ExecuteAsync(
      connection,
      sql,
      new { CutoffDate = cutoffDate },
      cancellationToken: cancellationToken);
  }
}
