namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Strongly-typed detail models for perspective status history entries.
/// Replaces anonymous types for AOT compatibility.
/// </summary>

/// <summary>
/// Details for OrderCreatedEvent status history
/// </summary>
public record OrderCreatedDetails {
  public decimal TotalAmount { get; init; }
  public int ItemCount { get; init; }
}

/// <summary>
/// Details for InventoryReservedEvent status history
/// </summary>
public record InventoryReservedDetails {
  public required string ProductId { get; init; }
  public int Quantity { get; init; }
  public DateTime ReservedAt { get; init; }
}

/// <summary>
/// Details for PaymentProcessedEvent status history
/// </summary>
public record PaymentProcessedDetails {
  public required string TransactionId { get; init; }
  public decimal Amount { get; init; }
}

/// <summary>
/// Details for OrderShippedEvent status history
/// </summary>
public record OrderShippedDetails {
  public required string ShipmentId { get; init; }
  public required string TrackingNumber { get; init; }
}

/// <summary>
/// Details for PaymentFailedEvent status history
/// </summary>
public record PaymentFailedDetails {
  public required string Reason { get; init; }
}
