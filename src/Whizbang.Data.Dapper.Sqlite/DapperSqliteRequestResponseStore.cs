using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of IRequestResponseStore using Dapper.
/// </summary>
public class DapperSqliteRequestResponseStore : DapperRequestResponseStoreBase {
  public DapperSqliteRequestResponseStore(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    JsonSerializerOptions jsonOptions)
    : base(connectionFactory, executor, jsonOptions) {
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
    VALUES (@CorrelationId, '00000000-0000-0000-0000-000000000000', @ResponseEnvelope, datetime('now', '+1 day'), datetime('now'))
    ON CONFLICT(correlation_id)
    DO UPDATE SET response_envelope = @ResponseEnvelope";

  protected override string GetCleanupExpiredSql() => @"
    DELETE FROM whizbang_request_response
    WHERE expires_at < @Now";
}
