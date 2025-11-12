using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based IRequestResponseStore implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
public abstract class DapperRequestResponseStoreBase : IRequestResponseStore {
  protected readonly IDbConnectionFactory _connectionFactory;
  protected readonly IDbExecutor _executor;
  protected readonly JsonSerializerOptions _jsonOptions;

  protected DapperRequestResponseStoreBase(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
    _jsonOptions = new JsonSerializerOptions {
      PropertyNameCaseInsensitive = true,
      PropertyNamingPolicy = null, // Preserve PascalCase property names
      WriteIndented = false,
      DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never, // Include all properties
      IncludeFields = true // Include fields as well as properties
    };
  }

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
  protected abstract string GetSaveRequestSql();

  /// <summary>
  /// Gets the SQL query to retrieve request/response data for waiting.
  /// Should return: ResponseEnvelope (string, nullable), ExpiresAt (DateTimeOffset)
  /// Parameters: @CorrelationId (Guid)
  /// </summary>
  protected abstract string GetWaitForResponseSql();

  /// <summary>
  /// Gets the SQL command to save a response.
  /// Parameters: @CorrelationId (Guid), @ResponseEnvelope (string)
  /// </summary>
  protected abstract string GetSaveResponseSql();

  /// <summary>
  /// Gets the SQL command to cleanup expired requests.
  /// Parameters: @Now (DateTimeOffset)
  /// </summary>
  protected abstract string GetCleanupExpiredSql();

  public async Task SaveRequestAsync(CorrelationId correlationId, MessageId requestId, TimeSpan timeout, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetSaveRequestSql();
    var expiresAt = DateTimeOffset.UtcNow + timeout;

    await _executor.ExecuteAsync(
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

  [RequiresUnreferencedCode("JSON deserialization uses reflection")]
  public async Task<IMessageEnvelope?> WaitForResponseAsync(CorrelationId correlationId, CancellationToken cancellationToken = default) {
    try {
      var startTime = DateTimeOffset.UtcNow;

      while (!cancellationToken.IsCancellationRequested) {
        using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        EnsureConnectionOpen(connection);

        var sql = GetWaitForResponseSql();

        var row = await _executor.QuerySingleOrDefaultAsync<RequestResponseRow>(
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
          // Response is available
          return JsonSerializer.Deserialize<MessageEnvelope<object>>(row.ResponseEnvelope, _jsonOptions);
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

  [RequiresUnreferencedCode("JSON serialization uses reflection")]
  public async Task SaveResponseAsync(CorrelationId correlationId, IMessageEnvelope response, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(response);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    // Serialize using the actual runtime type to preserve all properties
    var json = JsonSerializer.Serialize(response, response.GetType(), _jsonOptions);
    var sql = GetSaveResponseSql();

    await _executor.ExecuteAsync(
      connection,
      sql,
      new {
        CorrelationId = correlationId.Value,
        ResponseEnvelope = json
      },
      cancellationToken: cancellationToken);
  }

  public async Task CleanupExpiredAsync(CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = GetCleanupExpiredSql();

    await _executor.ExecuteAsync(
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
