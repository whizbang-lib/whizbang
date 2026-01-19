namespace ECommerce.BFF.API.Hubs;

/// <summary>
/// Product notification sent to clients via SignalR when product catalog changes
/// </summary>
public record ProductNotification {
  /// <summary>
  /// Unique identifier for the product
  /// </summary>
  public required string ProductId { get; init; }

  /// <summary>
  /// Type of notification: "Created", "Updated", or "Deleted"
  /// </summary>
  public required string NotificationType { get; init; }

  /// <summary>
  /// Product name
  /// </summary>
  public required string Name { get; init; }

  /// <summary>
  /// Product description (optional)
  /// </summary>
  public string? Description { get; init; }

  /// <summary>
  /// Product price (optional)
  /// </summary>
  public decimal? Price { get; init; }

  /// <summary>
  /// Product image URL (optional)
  /// </summary>
  public string? ImageUrl { get; init; }

  /// <summary>
  /// Timestamp of when the notification was created (UTC)
  /// </summary>
  public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
