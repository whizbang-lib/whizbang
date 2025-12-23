using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of IRequestResponseStore using Dapper.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveRequestAsync_ShouldStoreRequestAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithoutResponse_ShouldTimeoutAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_WithNullResponse_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:CleanupExpiredAsync_ShouldNotThrowAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithCancellation_ShouldRespectCancellationAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
public class DapperSqliteRequestResponseStore(
  IDbConnectionFactory connectionFactory,
  IDbExecutor executor,
  JsonSerializerOptions jsonOptions) : DapperRequestResponseStoreBase(connectionFactory, executor, jsonOptions) {
  /// <summary>
  /// Returns the SQLite-specific SQL for saving a request using INSERT ON CONFLICT UPSERT.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveRequestAsync_ShouldStoreRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  protected override string GetSaveRequestSql() => @"
    INSERT INTO whizbang_request_response (correlation_id, request_id, response_envelope, expires_at, created_at)
    VALUES (@CorrelationId, @RequestId, NULL, @ExpiresAt, @CreatedAt)
    ON CONFLICT(correlation_id)
    DO UPDATE SET request_id = @RequestId, expires_at = @ExpiresAt";

  /// <summary>
  /// Returns the SQLite-specific SQL for waiting for and retrieving a response.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithoutResponse_ShouldTimeoutAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithCancellation_ShouldRespectCancellationAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  protected override string GetWaitForResponseSql() => @"
    SELECT response_envelope AS ResponseEnvelope, expires_at AS ExpiresAt
    FROM whizbang_request_response
    WHERE correlation_id = @CorrelationId";

  /// <summary>
  /// Returns the SQLite-specific SQL for saving a response using INSERT ON CONFLICT UPSERT.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_WithNullResponse_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  protected override string GetSaveResponseSql() => @"
    INSERT INTO whizbang_request_response (correlation_id, request_id, response_envelope, expires_at, created_at)
    VALUES (@CorrelationId, '00000000-0000-0000-0000-000000000000', @ResponseEnvelope, datetime('now', '+1 day'), datetime('now'))
    ON CONFLICT(correlation_id)
    DO UPDATE SET response_envelope = @ResponseEnvelope";

  /// <summary>
  /// Returns the SQLite-specific SQL for cleaning up expired request-response records.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:CleanupExpiredAsync_ShouldNotThrowAsync</tests>
  protected override string GetCleanupExpiredSql() => @"
    DELETE FROM whizbang_request_response
    WHERE expires_at < @Now";
}
