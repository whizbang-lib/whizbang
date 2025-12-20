using Whizbang.Core;

namespace ECommerce.BFF.API.Lenses;

/// <summary>
/// Data transfer object for inventory level information.
/// Maps to the bff.inventory_levels table materialized by InventoryLevelsPerspective.
/// </summary>
public record InventoryLevelDto {
  /// <summary>
  /// Product identifier
  /// </summary>
  [StreamKey]
  public Guid ProductId { get; init; }

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
