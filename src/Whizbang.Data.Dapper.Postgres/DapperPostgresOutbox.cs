using Whizbang.Core.Data;
using Whizbang.Core.Observability;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IOutbox using Dapper.
/// Uses JSONB storage pattern for envelope serialization.
/// Supports both immediate processing (with instanceId) and polling-based (without instanceId).
/// </summary>
public class DapperPostgresOutbox(
  IDbConnectionFactory connectionFactory,
  IDbExecutor executor,
  IJsonbPersistenceAdapter<IMessageEnvelope> envelopeAdapter,
  Guid? instanceId = null,
  int leaseSeconds = 300
) : DapperOutboxBase(connectionFactory, executor, envelopeAdapter, instanceId, leaseSeconds) {
  protected override string GetStoreSql() => @"
    INSERT INTO wb_outbox (
      message_id, destination, event_type, event_data, metadata, scope,
      status, attempts, error, created_at, published_at,
      instance_id, lease_expiry
    )
    VALUES (
      @MessageId, @Destination, @EventType, @EventData::jsonb, @Metadata::jsonb, @Scope::jsonb,
      @Status, @Attempts, NULL, @CreatedAt, NULL,
      @InstanceId, @LeaseExpiry
    )";

  protected override string GetPendingSql() => @"
    SELECT
      message_id AS MessageId,
      destination AS Destination,
      event_type AS EventType,
      event_data::text AS EventData,
      metadata::text AS Metadata,
      scope::text AS Scope,
      created_at AS CreatedAt
    FROM wb_outbox
    WHERE status = 'Pending'
    ORDER BY created_at
    LIMIT @BatchSize";

  protected override string GetMarkPublishedSql() => @"
    UPDATE wb_outbox
    SET status = 'Published', published_at = @PublishedAt
    WHERE message_id = @MessageId";
}
