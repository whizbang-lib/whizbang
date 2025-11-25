using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using ECommerce.Contracts.Generated;
using Whizbang.Core.Data;

namespace ECommerce.BFF.API.Lenses;

/// <summary>
/// Dapper-based implementation of IOrderLens for fast readonly queries.
/// Queries order_perspective table using JSONB pattern (model_data, metadata, scope).
/// </summary>
public class OrderLens : IOrderLens {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<OrderLens> _logger;

  public OrderLens(IDbConnectionFactory connectionFactory, ILogger<OrderLens> logger) {
    _connectionFactory = connectionFactory;
    _logger = logger;
  }

  public async Task<OrderReadModel?> GetByIdAsync(string orderId, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        model_data::text AS ModelDataJson
      FROM order_perspective
      WHERE id = @OrderId::uuid";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var modelDataJson = await connection.QuerySingleOrDefaultAsync<string>(sql, new { OrderId = orderId });

    return modelDataJson == null ? null : DeserializeOrderReadModel(modelDataJson);
  }

  public async Task<IEnumerable<OrderReadModel>> GetByCustomerIdAsync(string customerId, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        model_data::text AS ModelDataJson
      FROM order_perspective
      WHERE scope->>'CustomerId' = @CustomerId
      ORDER BY created_at DESC";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var results = await connection.QueryAsync<string>(sql, new { CustomerId = customerId });

    return results.Select(DeserializeOrderReadModel);
  }

  public async Task<IEnumerable<OrderReadModel>> GetByTenantIdAsync(string tenantId, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        model_data::text AS ModelDataJson
      FROM order_perspective
      WHERE scope->>'TenantId' = @TenantId
      ORDER BY created_at DESC";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var results = await connection.QueryAsync<string>(sql, new { TenantId = tenantId });

    return results.Select(DeserializeOrderReadModel);
  }

  public async Task<IEnumerable<OrderReadModel>> GetRecentOrdersAsync(int limit = 100, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        model_data::text AS ModelDataJson
      FROM order_perspective
      ORDER BY created_at DESC
      LIMIT @Limit";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var results = await connection.QueryAsync<string>(sql, new { Limit = limit });

    return results.Select(DeserializeOrderReadModel);
  }

  public async Task<IEnumerable<OrderReadModel>> GetByStatusAsync(string tenantId, string status, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        model_data::text AS ModelDataJson
      FROM order_perspective
      WHERE scope->>'TenantId' = @TenantId
        AND model_data->>'Status' = @Status
      ORDER BY created_at DESC";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var results = await connection.QueryAsync<string>(sql, new { TenantId = tenantId, Status = status });

    return results.Select(DeserializeOrderReadModel);
  }

  public async Task<IEnumerable<OrderStatusHistory>> GetStatusHistoryAsync(string orderId, CancellationToken cancellationToken = default) {
    // NOTE: With JSONB pattern, we no longer have a separate status history table.
    // The metadata column contains the EventType for each update.
    // For now, return empty list. In the future, could query event store for full history.
    _logger.LogWarning("GetStatusHistoryAsync called but status history table no longer exists with JSONB pattern. Returning empty list.");
    return Enumerable.Empty<OrderStatusHistory>();
  }

  // Helper method to deserialize JSONB model_data to OrderReadModel
  private static OrderReadModel DeserializeOrderReadModel(string modelDataJson) {
    var orderReadModel = JsonSerializer.Deserialize<OrderReadModel>(
      modelDataJson,
      WhizbangJsonContext.CreateOptions()
    );

    return orderReadModel ?? throw new InvalidOperationException("Failed to deserialize OrderReadModel from JSONB");
  }

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
