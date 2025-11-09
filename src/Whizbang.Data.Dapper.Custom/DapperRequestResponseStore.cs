using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Dapper-based implementation of IRequestResponseStore for request/response correlation.
/// Enables request-response pattern on transports that don't natively support it (Kafka, Event Hubs).
/// </summary>
public class DapperRequestResponseStore : IRequestResponseStore {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IDbExecutor _executor;
  private readonly JsonSerializerOptions _jsonOptions;

  public DapperRequestResponseStore(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
    _jsonOptions = new JsonSerializerOptions {
      PropertyNameCaseInsensitive = true
    };
  }

  public async Task SaveRequestAsync(CorrelationId correlationId, MessageId requestId, TimeSpan timeout, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    const string sql = @"
      INSERT INTO whizbang_request_response (correlation_id, request_id, response_envelope, expires_at, created_at)
      VALUES (@CorrelationId, @RequestId, NULL, @ExpiresAt, @CreatedAt)";

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

  public async Task<IMessageEnvelope?> WaitForResponseAsync(CorrelationId correlationId, CancellationToken cancellationToken = default) {
    var startTime = DateTimeOffset.UtcNow;

    while (!cancellationToken.IsCancellationRequested) {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      connection.Open();

      const string sql = @"
        SELECT response_envelope AS ResponseEnvelope, expires_at AS ExpiresAt
        FROM whizbang_request_response
        WHERE correlation_id = @CorrelationId";

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
  }

  public async Task SaveResponseAsync(CorrelationId correlationId, IMessageEnvelope response, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(response);

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    connection.Open();

    var json = JsonSerializer.Serialize(response, _jsonOptions);

    const string sql = @"
      UPDATE whizbang_request_response
      SET response_envelope = @ResponseEnvelope
      WHERE correlation_id = @CorrelationId";

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
    connection.Open();

    const string sql = @"
      DELETE FROM whizbang_request_response
      WHERE expires_at < @Now";

    await _executor.ExecuteAsync(
      connection,
      sql,
      new { Now = DateTimeOffset.UtcNow },
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Internal row structure for Dapper mapping.
  /// </summary>
  private class RequestResponseRow {
    public string? ResponseEnvelope { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
  }
}
