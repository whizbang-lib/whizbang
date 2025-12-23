using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IRequestResponseStore using Dapper.
/// </summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveRequestAsync_ShouldStoreRequestAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:WaitForResponseAsync_WithoutResponse_ShouldTimeoutAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveResponseAsync_WithNullResponse_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:CleanupExpiredAsync_ShouldNotThrowAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:WaitForResponseAsync_WithCancellation_ShouldRespectCancellationAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
public class DapperPostgresRequestResponseStore(
  IDbConnectionFactory connectionFactory,
  IDbExecutor executor,
  JsonSerializerOptions jsonOptions) : DapperRequestResponseStoreBase(connectionFactory, executor, jsonOptions) {
  /// <summary>
  /// Returns the PostgreSQL-specific SQL for saving a request record with UPSERT logic.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveRequestAsync_ShouldStoreRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  protected override string GetSaveRequestSql() => @"
    INSERT INTO wh_request_response (request_id, correlation_id, request_type, request_data, status, created_at, expires_at)
    VALUES (@RequestId, @CorrelationId, 'Request', '{}'::jsonb, 'Pending', @CreatedAt, @ExpiresAt)
    ON CONFLICT(correlation_id)
    DO UPDATE SET request_id = @RequestId, expires_at = @ExpiresAt, status = 'Pending'";

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for querying a response by correlation ID.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:WaitForResponseAsync_WithoutResponse_ShouldTimeoutAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:WaitForResponseAsync_WithCancellation_ShouldRespectCancellationAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  protected override string GetWaitForResponseSql() => @"
    SELECT response_data::text AS ResponseEnvelope, expires_at AS ExpiresAt
    FROM wh_request_response
    WHERE correlation_id = @CorrelationId";

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for saving a response with UPSERT logic.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveResponseAsync_WithNullResponse_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  protected override string GetSaveResponseSql() => @"
    INSERT INTO wh_request_response (request_id, correlation_id, request_type, request_data, response_type, response_data, status, created_at, completed_at, expires_at)
    VALUES (gen_random_uuid(), @CorrelationId, 'Request', '{}'::jsonb, 'Response', @ResponseEnvelope::jsonb, 'Completed', NOW(), NOW(), NOW() + INTERVAL '1 day')
    ON CONFLICT(correlation_id)
    DO UPDATE SET
      response_type = 'Response',
      response_data = @ResponseEnvelope::jsonb,
      status = 'Completed',
      completed_at = NOW()";

  /// <summary>
  /// Returns the PostgreSQL-specific SQL for deleting expired request-response records.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs:CleanupExpiredAsync_ShouldNotThrowAsync</tests>
  protected override string GetCleanupExpiredSql() => @"
    DELETE FROM wh_request_response
    WHERE expires_at < @Now";
}
