using Whizbang;
using Whizbang.Core;

namespace ECommerce.Contracts.Lenses;

/// <summary>
/// Data transfer object for inventory level information.
/// Shared lens model used by both BFF.API and InventoryWorker perspectives.
/// Maps to perspective-specific tables (e.g., bff.inventory_levels, inventory.inventory_levels).
/// </summary>
[WhizbangSerializable]
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
