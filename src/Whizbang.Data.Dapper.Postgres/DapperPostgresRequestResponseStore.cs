using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IRequestResponseStore using Dapper.
/// </summary>
public class DapperPostgresRequestResponseStore : DapperRequestResponseStoreBase {
  public DapperPostgresRequestResponseStore(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    JsonSerializerContext jsonContext)
    : base(connectionFactory, executor, jsonContext) {
  }

  protected override string GetSaveRequestSql() => @"
    INSERT INTO whizbang_request_response (correlation_id, request_id, response_envelope, expires_at, created_at)
    VALUES (@CorrelationId, @RequestId, NULL, @ExpiresAt, @CreatedAt)
    ON CONFLICT(correlation_id)
    DO UPDATE SET request_id = @RequestId, expires_at = @ExpiresAt";

  protected override string GetWaitForResponseSql() => @"
    SELECT response_envelope AS ResponseEnvelope, expires_at AS ExpiresAt
    FROM whizbang_request_response
    WHERE correlation_id = @CorrelationId";

  protected override string GetSaveResponseSql() => @"
    INSERT INTO whizbang_request_response (correlation_id, request_id, response_envelope, expires_at, created_at)
    VALUES (@CorrelationId, '00000000-0000-0000-0000-000000000000', @ResponseEnvelope, NOW() + INTERVAL '1 day', NOW())
    ON CONFLICT(correlation_id)
    DO UPDATE SET response_envelope = @ResponseEnvelope";

  protected override string GetCleanupExpiredSql() => @"
    DELETE FROM whizbang_request_response
    WHERE expires_at < @Now";
}
