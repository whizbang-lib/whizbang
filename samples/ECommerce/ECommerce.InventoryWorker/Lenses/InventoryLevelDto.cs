namespace ECommerce.InventoryWorker.Lenses;

/// <summary>
/// Data transfer object for inventory level information.
/// Maps to the inventory_levels table materialized by InventoryLevelsPerspective.
/// </summary>
public record InventoryLevelDto {
  /// <summary>
  /// Product identifier
  /// </summary>
  public string ProductId { get; init; } = string.Empty;

  /// <summary>
  /// Total quantity in inventory
  /// </summary>
  public int Quantity { get; init; }

  /// <summary>
  /// Quantity reserved for pending orders
  /// </summary>
  public int Reserved { get; init; }

  /// <summary>
  /// Available quantity (computed: Quantity - Reserved)
  /// </summary>
  public int Available { get; init; }

  /// <summary>
  /// When inventory was last updated
  /// </summary>
  public DateTime LastUpdated { get; init; }
}
