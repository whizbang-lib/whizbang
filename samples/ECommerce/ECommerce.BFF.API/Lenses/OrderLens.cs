using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using ECommerce.Contracts.Generated;
using Whizbang.Core.Data;

namespace ECommerce.BFF.API.Lenses;

/// <summary>
/// Dapper-based implementation of IOrderLens for fast readonly queries.
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
        order_id AS OrderId,
        customer_id AS CustomerId,
        tenant_id AS TenantId,
        status AS Status,
        total_amount AS TotalAmount,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt,
        item_count AS ItemCount,
        payment_status AS PaymentStatus,
        shipment_id AS ShipmentId,
        tracking_number AS TrackingNumber,
        line_items AS LineItemsJson
      FROM bff.orders
      WHERE order_id = @OrderId";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var result = await connection.QuerySingleOrDefaultAsync<OrderRow>(sql, new { OrderId = orderId });

    return result == null ? null : MapToReadModel(result);
  }

  public async Task<IEnumerable<OrderReadModel>> GetByCustomerIdAsync(string customerId, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        order_id AS OrderId,
        customer_id AS CustomerId,
        tenant_id AS TenantId,
        status AS Status,
        total_amount AS TotalAmount,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt,
        item_count AS ItemCount,
        payment_status AS PaymentStatus,
        shipment_id AS ShipmentId,
        tracking_number AS TrackingNumber,
        line_items AS LineItemsJson
      FROM bff.orders
      WHERE customer_id = @CustomerId
      ORDER BY created_at DESC";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var results = await connection.QueryAsync<OrderRow>(sql, new { CustomerId = customerId });

    return results.Select(MapToReadModel);
  }

  public async Task<IEnumerable<OrderReadModel>> GetByTenantIdAsync(string tenantId, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        order_id AS OrderId,
        customer_id AS CustomerId,
        tenant_id AS TenantId,
        status AS Status,
        total_amount AS TotalAmount,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt,
        item_count AS ItemCount,
        payment_status AS PaymentStatus,
        shipment_id AS ShipmentId,
        tracking_number AS TrackingNumber,
        line_items AS LineItemsJson
      FROM bff.orders
      WHERE tenant_id = @TenantId
      ORDER BY created_at DESC";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var results = await connection.QueryAsync<OrderRow>(sql, new { TenantId = tenantId });

    return results.Select(MapToReadModel);
  }

  public async Task<IEnumerable<OrderReadModel>> GetRecentOrdersAsync(int limit = 100, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        order_id AS OrderId,
        customer_id AS CustomerId,
        tenant_id AS TenantId,
        status AS Status,
        total_amount AS TotalAmount,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt,
        item_count AS ItemCount,
        payment_status AS PaymentStatus,
        shipment_id AS ShipmentId,
        tracking_number AS TrackingNumber,
        line_items AS LineItemsJson
      FROM bff.orders
      ORDER BY created_at DESC
      LIMIT @Limit";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var results = await connection.QueryAsync<OrderRow>(sql, new { Limit = limit });

    return results.Select(MapToReadModel);
  }

  public async Task<IEnumerable<OrderReadModel>> GetByStatusAsync(string tenantId, string status, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        order_id AS OrderId,
        customer_id AS CustomerId,
        tenant_id AS TenantId,
        status AS Status,
        total_amount AS TotalAmount,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt,
        item_count AS ItemCount,
        payment_status AS PaymentStatus,
        shipment_id AS ShipmentId,
        tracking_number AS TrackingNumber,
        line_items AS LineItemsJson
      FROM bff.orders
      WHERE tenant_id = @TenantId AND status = @Status
      ORDER BY created_at DESC";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var results = await connection.QueryAsync<OrderRow>(sql, new { TenantId = tenantId, Status = status });

    return results.Select(MapToReadModel);
  }

  public async Task<IEnumerable<OrderStatusHistory>> GetStatusHistoryAsync(string orderId, CancellationToken cancellationToken = default) {
    const string sql = @"
      SELECT
        id AS Id,
        order_id AS OrderId,
        status AS Status,
        event_type AS EventType,
        timestamp AS Timestamp,
        details AS Details
      FROM bff.order_status_history
      WHERE order_id = @OrderId
      ORDER BY timestamp ASC";

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);
    var results = await connection.QueryAsync<OrderStatusHistory>(sql, new { OrderId = orderId });

    return results;
  }

  // Helper method to map database row to read model
  private static OrderReadModel MapToReadModel(OrderRow row) {
    var lineItems = string.IsNullOrEmpty(row.LineItemsJson)
      ? new List<LineItemReadModel>()
      : JsonSerializer.Deserialize(
          row.LineItemsJson,
          (JsonTypeInfo<List<LineItemReadModel>>)WhizbangJsonContext.CreateOptions().GetTypeInfo(typeof(List<LineItemReadModel>))!
        ) ?? new List<LineItemReadModel>();

    return new OrderReadModel {
      OrderId = row.OrderId,
      CustomerId = row.CustomerId,
      TenantId = row.TenantId,
      Status = row.Status,
      TotalAmount = row.TotalAmount,
      CreatedAt = row.CreatedAt,
      UpdatedAt = row.UpdatedAt,
      ItemCount = row.ItemCount,
      PaymentStatus = row.PaymentStatus,
      ShipmentId = row.ShipmentId,
      TrackingNumber = row.TrackingNumber,
      LineItems = lineItems
    };
  }

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }

  // Database row DTO for Dapper mapping
  private class OrderRow {
    public required string OrderId { get; init; }
    public required string CustomerId { get; init; }
    public string? TenantId { get; init; }
    public required string Status { get; init; }
    public decimal TotalAmount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public int ItemCount { get; init; }
    public string? PaymentStatus { get; init; }
    public string? ShipmentId { get; init; }
    public string? TrackingNumber { get; init; }
    public required string LineItemsJson { get; init; }
  }
}
