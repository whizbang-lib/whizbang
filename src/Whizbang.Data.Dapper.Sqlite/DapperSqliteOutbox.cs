using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of IOutbox using Dapper.
/// </summary>
public class DapperSqliteOutbox : DapperOutboxBase {
  public DapperSqliteOutbox(IDbConnectionFactory connectionFactory, IDbExecutor executor)
    : base(connectionFactory, executor) {
  }

  protected override string GetStoreSql() => @"
    INSERT INTO whizbang_outbox (message_id, destination, payload, created_at, published_at)
    VALUES (@MessageId, @Destination, @Payload, @CreatedAt, NULL)";

  protected override string GetPendingSql() => @"
    SELECT message_id AS MessageId, destination AS Destination, payload AS Payload, created_at AS CreatedAt
    FROM whizbang_outbox
    WHERE published_at IS NULL
    ORDER BY created_at
    LIMIT @BatchSize";

  protected override string GetMarkPublishedSql() => @"
    UPDATE whizbang_outbox
    SET published_at = @PublishedAt
    WHERE message_id = @MessageId";
}
