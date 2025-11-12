using System.Data;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IInbox implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
public abstract class DapperInboxBase : IInbox {
  protected readonly IDbConnectionFactory _connectionFactory;
  protected readonly IDbExecutor _executor;

  protected DapperInboxBase(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
  }

  /// <summary>
  /// Ensures the connection is open. Handles both pre-opened and closed connections.
  /// </summary>
  protected static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }

  /// <summary>
  /// Gets the SQL query to check if a message has been processed.
  /// Should return count of matching records.
  /// Parameters: @MessageId (Guid)
  /// </summary>
  protected abstract string GetHasProcessedSql();

  /// <summary>
  /// Gets the SQL command to mark a message as processed.
  /// Should handle duplicate message IDs gracefully (upsert/ignore).
  /// Parameters: @MessageId (Guid), @HandlerName (string), @ProcessedAt (DateTimeOffset)
  /// </summary>
  protected abstract string GetMarkProcessedSql();

  /// <summary>
  /// Gets the SQL command to delete expired inbox records.
  /// Parameters: @CutoffDate (DateTimeOffset)
  /// </summary>
  protected abstract string GetCleanupExpiredSql();

  public async Task<bool> HasProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetHasProcessedSql();

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
    EnsureConnectionOpen(connection);

    var sql = GetMarkProcessedSql();

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
    EnsureConnectionOpen(connection);

    var sql = GetCleanupExpiredSql();
    var cutoffDate = DateTimeOffset.UtcNow - retention;

    await _executor.ExecuteAsync(
      connection,
      sql,
      new { CutoffDate = cutoffDate },
      cancellationToken: cancellationToken);
  }
}
