using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IRequestResponseStore implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
public abstract class DapperRequestResponseStoreBase : IRequestResponseStore {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IDbExecutor _executor;
  private readonly JsonSerializerOptions _jsonOptions;

  protected DapperRequestResponseStoreBase(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    JsonSerializerOptions jsonOptions
  ) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);
    ArgumentNullException.ThrowIfNull(jsonOptions);

    _connectionFactory = connectionFactory;
    _executor = executor;
    _jsonOptions = jsonOptions;
  }

  protected IDbConnectionFactory ConnectionFactory => _connectionFactory;
  protected IDbExecutor Executor => _executor;
  protected JsonSerializerOptions JsonOptions => _jsonOptions;

  /// <summary>
  /// Ensures the connection is open. Handles both pre-opened and closed connections.
  /// </summary>
  protected static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }

  /// <summary>
  /// Gets the SQL command to save a new request.
  /// Parameters: @CorrelationId (Guid), @RequestId (Guid), @ExpiresAt (DateTimeOffset), @CreatedAt (DateTimeOffset)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveRequestAsync_ShouldStoreRequestAsync</tests>
  protected abstract string GetSaveRequestSql();

  /// <summary>
  /// Gets the SQL query to retrieve request/response data for waiting.
  /// Should return: ResponseEnvelope (string, nullable), ExpiresAt (DateTimeOffset)
  /// Parameters: @CorrelationId (Guid)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithoutResponse_ShouldTimeoutAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithCancellation_ShouldRespectCancellationAsync</tests>
  protected abstract string GetWaitForResponseSql();

  /// <summary>
  /// Gets the SQL command to save a response.
  /// Parameters: @CorrelationId (Guid), @ResponseEnvelope (string)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_WithNullResponse_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  protected abstract string GetSaveResponseSql();

  /// <summary>
  /// Gets the SQL command to cleanup expired requests.
  /// Parameters: @Now (DateTimeOffset)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:CleanupExpiredAsync_ShouldNotThrowAsync</tests>
  protected abstract string GetCleanupExpiredSql();

  /// <summary>
  /// Saves a new request with expiration timeout.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveRequestAsync_ShouldStoreRequestAsync</tests>
  public async Task SaveRequestAsync(CorrelationId correlationId, MessageId requestId, TimeSpan timeout, CancellationToken cancellationToken = default) {
    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetSaveRequestSql();
    var expiresAt = DateTimeOffset.UtcNow + timeout;

    await Executor.ExecuteAsync(
      connection,
      sql,
      new {
        CorrelationId = correlationId.Value,
        RequestId = requestId.Value,
        ExpiresAt = expiresAt,
        CreatedAt = DateTimeOffset.UtcNow
      },
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Non-generic WaitForResponseAsync is not supported for AOT compatibility.
  /// Use the generic WaitForResponseAsync&lt;TMessage&gt; method instead.
  /// </summary>
  public Task<IMessageEnvelope?> WaitForResponseAsync(CorrelationId correlationId, CancellationToken cancellationToken = default) {
    throw new NotSupportedException(
      "Non-generic WaitForResponseAsync is not supported for request/response stores in AOT scenarios. " +
      "Use WaitForResponseAsync<TMessage> with the concrete message type instead.");
  }

  /// <summary>
  /// Waits for a response to arrive for a given correlation ID.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithoutResponse_ShouldTimeoutAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithCancellation_ShouldRespectCancellationAsync</tests>
  public async Task<MessageEnvelope<TMessage>?> WaitForResponseAsync<TMessage>(CorrelationId correlationId, CancellationToken cancellationToken = default) {
    try {
      var startTime = DateTimeOffset.UtcNow;

      while (!cancellationToken.IsCancellationRequested) {
        using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
        EnsureConnectionOpen(connection);

        var sql = GetWaitForResponseSql();

        var row = await Executor.QuerySingleOrDefaultAsync<RequestResponseRow>(
          connection,
          sql,
          new { CorrelationId = correlationId.Value },
          cancellationToken: cancellationToken);

        if (row == null) {
          // Request not found - may have been cleaned up
          return null;
        }

        if (DateTimeOffset.UtcNow >= row.ExpiresAt) {
          // Request has expired
          return null;
        }

        if (!string.IsNullOrEmpty(row.ResponseEnvelope)) {
          // Response is available - deserialize with concrete type (AOT-compatible)
          var envelopeType = typeof(MessageEnvelope<TMessage>);
          var typeInfo = JsonOptions.GetTypeInfo(envelopeType) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
          return JsonSerializer.Deserialize(row.ResponseEnvelope, typeInfo) as MessageEnvelope<TMessage>;
        }

        // Wait a bit before polling again
        await Task.Delay(50, cancellationToken);
      }

      return null;
    } catch (TaskCanceledException) {
      // Return null when cancelled (contract expects null, not exception)
      return null;
    } catch (OperationCanceledException) {
      // Return null when cancelled (contract expects null, not exception)
      return null;
    }
  }

  /// <summary>
  /// Saves a response for a waiting request.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_WithNullResponse_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  public async Task SaveResponseAsync(CorrelationId correlationId, IMessageEnvelope response, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(response);

    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    // Serialize using the actual runtime type to preserve all properties (AOT-compatible)
    var responseType = response.GetType();
    var typeInfo = JsonOptions.GetTypeInfo(responseType) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {responseType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
    var json = JsonSerializer.Serialize(response, typeInfo);
    var sql = GetSaveResponseSql();

    await Executor.ExecuteAsync(
      connection,
      sql,
      new {
        CorrelationId = correlationId.Value,
        ResponseEnvelope = json
      },
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Cleans up expired request/response pairs.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:CleanupExpiredAsync_ShouldNotThrowAsync</tests>
  public async Task CleanupExpiredAsync(CancellationToken cancellationToken = default) {
    using var connection = await ConnectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetCleanupExpiredSql();

    await Executor.ExecuteAsync(
      connection,
      sql,
      new { Now = DateTimeOffset.UtcNow },
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Internal row structure for Dapper mapping.
  /// </summary>
  protected class RequestResponseRow {
    public string? ResponseEnvelope { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
  }
}
