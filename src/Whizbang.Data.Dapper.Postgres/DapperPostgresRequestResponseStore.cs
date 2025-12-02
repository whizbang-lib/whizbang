using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IRequestResponseStore using Dapper.
/// </summary>
public class DapperPostgresRequestResponseStore(
  IDbConnectionFactory connectionFactory,
  IDbExecutor executor,
  JsonSerializerOptions jsonOptions) : DapperRequestResponseStoreBase(connectionFactory, executor, jsonOptions) {
  protected override string GetSaveRequestSql() => @"
    INSERT INTO whizbang_request_response (request_id, correlation_id, request_type, request_data, status, created_at, expires_at)
    VALUES (@RequestId, @CorrelationId, 'Request', '{}'::jsonb, 'Pending', @CreatedAt, @ExpiresAt)
    ON CONFLICT(correlation_id)
    DO UPDATE SET request_id = @RequestId, expires_at = @ExpiresAt, status = 'Pending'";

  protected override string GetWaitForResponseSql() => @"
    SELECT response_data::text AS ResponseEnvelope, expires_at AS ExpiresAt
    FROM whizbang_request_response
    WHERE correlation_id = @CorrelationId";

  protected override string GetSaveResponseSql() => @"
    INSERT INTO whizbang_request_response (request_id, correlation_id, request_type, request_data, response_type, response_data, status, created_at, completed_at, expires_at)
    VALUES (gen_random_uuid(), @CorrelationId, 'Request', '{}'::jsonb, 'Response', @ResponseEnvelope::jsonb, 'Completed', NOW(), NOW(), NOW() + INTERVAL '1 day')
    ON CONFLICT(correlation_id)
    DO UPDATE SET
      response_type = 'Response',
      response_data = @ResponseEnvelope::jsonb,
      status = 'Completed',
      completed_at = NOW()";

  protected override string GetCleanupExpiredSql() => @"
    DELETE FROM whizbang_request_response
    WHERE expires_at < @Now";
}
