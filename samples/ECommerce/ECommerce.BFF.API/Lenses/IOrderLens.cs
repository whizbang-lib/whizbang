namespace ECommerce.BFF.API.Lenses;

/// <summary>
/// Readonly repository (Lens) for querying order read models.
/// Lenses provide fast, denormalized queries optimized for UI needs.
/// </summary>
public interface IOrderLens {
  /// <summary>
  /// Get a specific order by ID with full details
  /// </summary>
  Task<OrderReadModel?> GetByIdAsync(string orderId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Get all orders for a specific customer
  /// </summary>
  Task<IEnumerable<OrderReadModel>> GetByCustomerIdAsync(string customerId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Get all orders for a tenant (admin view)
  /// </summary>
  Task<IEnumerable<OrderReadModel>> GetByTenantIdAsync(string tenantId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Get recent orders across all tenants (super-admin view)
  /// </summary>
  Task<IEnumerable<OrderReadModel>> GetRecentOrdersAsync(int limit = 100, CancellationToken cancellationToken = default);

  /// <summary>
  /// Get orders by status for a tenant
  /// </summary>
  Task<IEnumerable<OrderReadModel>> GetByStatusAsync(string tenantId, string status, CancellationToken cancellationToken = default);

  /// <summary>
  /// Get order status history for tracking timeline
  /// </summary>
  Task<IEnumerable<OrderStatusHistory>> GetStatusHistoryAsync(string orderId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Order read model - denormalized view optimized for queries
/// </summary>
public record OrderReadModel {
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

  // Line items stored as JSONB in database
  public List<LineItemReadModel> LineItems { get; init; } = new();
}

/// <summary>
/// Line item read model
/// </summary>
public record LineItemReadModel {
  public required string ProductId { get; init; }
  public required string ProductName { get; init; }
  public int Quantity { get; init; }
  public decimal Price { get; init; }
}

/// <summary>
/// Order status history entry
/// </summary>
public record OrderStatusHistory {
  public int Id { get; init; }
  public required string OrderId { get; init; }
  public required string Status { get; init; }
  public required string EventType { get; init; }
  public DateTime Timestamp { get; init; }
  public string? Details { get; init; }  // JSONB
}
