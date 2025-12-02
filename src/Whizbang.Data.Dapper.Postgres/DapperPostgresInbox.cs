using Whizbang.Core.Data;
using Whizbang.Core.Observability;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IInbox using Dapper.
/// Uses JSONB storage pattern for envelope serialization - mirrors DapperPostgresOutbox.
/// Supports both immediate processing (with instanceId) and polling-based (without instanceId).
/// </summary>
public class DapperPostgresInbox(
  IDbConnectionFactory connectionFactory,
  IDbExecutor executor,
  IJsonbPersistenceAdapter<IMessageEnvelope> envelopeAdapter,
  Guid? instanceId = null,
  int leaseSeconds = 300) : DapperInboxBase(connectionFactory, executor, envelopeAdapter, instanceId, leaseSeconds) {
  protected override string GetStoreSql() => @"
    INSERT INTO wb_inbox (
      message_id, handler_name, event_type, event_data, metadata, scope,
      status, attempts, received_at, processed_at, instance_id, lease_expiry
    ) VALUES (
      @MessageId, @HandlerName, @EventType, @EventData::jsonb, @Metadata::jsonb, @Scope::jsonb,
      @Status, @Attempts, @ReceivedAt, NULL, @InstanceId, @LeaseExpiry
    )
    ON CONFLICT (message_id) DO NOTHING";

  protected override string GetPendingSql() => @"
    SELECT
      message_id AS MessageId,
      handler_name AS HandlerName,
      event_type AS EventType,
      event_data::text AS EventData,
      metadata::text AS Metadata,
      scope::text AS Scope,
      received_at AS ReceivedAt
    FROM wb_inbox
    WHERE status = 'Pending'
    ORDER BY received_at
    LIMIT @BatchSize";

  protected override string GetMarkProcessedSql() => @"
    UPDATE wb_inbox
    SET status = 'Completed',
        processed_at = @ProcessedAt
    WHERE message_id = @MessageId";

  protected override string GetHasProcessedSql() => @"
    SELECT COUNT(1)
    FROM wb_inbox
    WHERE message_id = @MessageId
      AND processed_at IS NOT NULL";

  protected override string GetCleanupExpiredSql() => @"
    DELETE FROM wb_inbox
    WHERE status = 'Completed'
      AND processed_at < @CutoffDate";
}
