namespace ECommerce.BFF.API.Hubs;

/// <summary>
/// Inventory notification sent to clients via SignalR when inventory levels change
/// </summary>
public record InventoryNotification {
  /// <summary>
  /// Product ID for which inventory changed
  /// </summary>
  public required string ProductId { get; init; }

  /// <summary>
  /// Type of notification: "Restocked", "Reserved", or "Adjusted"
  /// </summary>
  public required string NotificationType { get; init; }

  /// <summary>
  /// Total quantity in stock
  /// </summary>
  public required int Quantity { get; init; }

  /// <summary>
  /// Quantity reserved (committed but not yet fulfilled)
  /// </summary>
  public required int Reserved { get; init; }

  /// <summary>
  /// Available quantity (quantity - reserved)
  /// </summary>
  public required int Available { get; init; }

  /// <summary>
  /// Timestamp of when the notification was created (UTC)
  /// </summary>
  public DateTime Timestamp { get; init; } = DateTime.UtcNow;

  /// <summary>
  /// Optional reason for the inventory change
  /// </summary>
  public string? Reason { get; init; }
}
