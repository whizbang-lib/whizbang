using Whizbang.Core.Data;
using Whizbang.Core.Observability;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IOutbox using Dapper.
/// Uses JSONB storage pattern for envelope serialization.
/// </summary>
public class DapperPostgresOutbox(
  IDbConnectionFactory connectionFactory,
  IDbExecutor executor,
  IJsonbPersistenceAdapter<IMessageEnvelope> envelopeAdapter) : DapperOutboxBase(connectionFactory, executor, envelopeAdapter) {
  protected override string GetStoreSql() => @"
    INSERT INTO whizbang_outbox (message_id, destination, event_type, event_data, metadata, scope, created_at, published_at)
    VALUES (@MessageId, @Destination, @EventType, @EventData::jsonb, @Metadata::jsonb, @Scope::jsonb, @CreatedAt, NULL)";

  protected override string GetPendingSql() => @"
    SELECT
      message_id AS MessageId,
      destination AS Destination,
      event_type AS EventType,
      event_data::text AS EventData,
      metadata::text AS Metadata,
      scope::text AS Scope,
      created_at AS CreatedAt
    FROM whizbang_outbox
    WHERE published_at IS NULL
    ORDER BY created_at
    LIMIT @BatchSize";

  protected override string GetMarkPublishedSql() => @"
    UPDATE whizbang_outbox
    SET published_at = @PublishedAt
    WHERE message_id = @MessageId";
}
