using Whizbang;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

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
  [StreamId]
  public Guid ProductId { get; init; }

  /// <summary>
  /// Total quantity in inventory
  /// </summary>
  /// <remarks>
  /// Indexed because the GraphQL surface offers sorting by it. Composed at request time, so no source
  /// here shows the ORDER BY that this serves.
  /// </remarks>
  [Indexed]
  public int Quantity { get; init; }

  /// <summary>
  /// Quantity reserved for pending orders
  /// </summary>
  [SuppressIndexAdvisory("a working count read alongside Available rather than ordered by")]
  public int Reserved { get; init; }

  /// <summary>
  /// Available quantity (computed: Quantity - Reserved)
  /// </summary>
  [Indexed]
  public int Available { get; init; }

  /// <summary>
  /// When inventory was last updated
  /// </summary>
  /// <remarks>
  /// Indexed because a stock screen is ordered by staleness, which is a sort over this field.
  /// </remarks>
  [Indexed]
  public DateTime LastUpdated { get; init; }
}
