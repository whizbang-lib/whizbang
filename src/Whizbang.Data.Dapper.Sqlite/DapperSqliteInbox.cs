using Whizbang.Core.Data;
using Whizbang.Core.Observability;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of IInbox using Dapper.
/// Uses JSONB columns (stored as TEXT in SQLite) for envelope persistence - mirrors DapperSqliteOutbox.
/// </summary>
public class DapperSqliteInbox : DapperInboxBase {
  public DapperSqliteInbox(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    IJsonbPersistenceAdapter<IMessageEnvelope> envelopeAdapter)
    : base(connectionFactory, executor, envelopeAdapter) {
  }

  protected override string GetStoreSql() => @"
    INSERT OR IGNORE INTO whizbang_inbox (message_id, handler_name, event_type, event_data, metadata, scope, received_at, processed_at)
    VALUES (@MessageId, @HandlerName, @EventType, @EventData, @Metadata, @Scope, @ReceivedAt, NULL)";

  protected override string GetPendingSql() => @"
    SELECT
      message_id AS MessageId,
      handler_name AS HandlerName,
      event_type AS EventType,
      event_data AS EventData,
      metadata AS Metadata,
      scope AS Scope,
      received_at AS ReceivedAt
    FROM whizbang_inbox
    WHERE processed_at IS NULL
    ORDER BY received_at
    LIMIT @BatchSize";

  protected override string GetMarkProcessedSql() => @"
    UPDATE whizbang_inbox
    SET processed_at = @ProcessedAt
    WHERE message_id = @MessageId";

  protected override string GetHasProcessedSql() => @"
    SELECT COUNT(1)
    FROM whizbang_inbox
    WHERE message_id = @MessageId
      AND processed_at IS NOT NULL";

  protected override string GetCleanupExpiredSql() => @"
    DELETE FROM whizbang_inbox
    WHERE processed_at < @CutoffDate";
}
