using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IInbox using Dapper.
/// </summary>
public class DapperPostgresInbox : DapperInboxBase {
  public DapperPostgresInbox(IDbConnectionFactory connectionFactory, IDbExecutor executor)
    : base(connectionFactory, executor) {
  }

  protected override string GetHasProcessedSql() => @"
    SELECT COUNT(1)
    FROM whizbang_inbox
    WHERE message_id = @MessageId";

  protected override string GetMarkProcessedSql() => @"
    INSERT INTO whizbang_inbox (message_id, handler_name, processed_at)
    VALUES (@MessageId, @HandlerName, @ProcessedAt)
    ON CONFLICT (message_id) DO NOTHING";

  protected override string GetCleanupExpiredSql() => @"
    DELETE FROM whizbang_inbox
    WHERE processed_at < @CutoffDate";
}
