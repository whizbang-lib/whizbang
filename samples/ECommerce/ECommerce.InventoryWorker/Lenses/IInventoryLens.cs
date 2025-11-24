namespace ECommerce.InventoryWorker.Lenses;

/// <summary>
/// Read-only query interface for inventory level data.
/// Queries the inventory_levels table materialized by InventoryLevelsPerspective.
/// </summary>
public interface IInventoryLens {
  /// <summary>
  /// Gets inventory levels for a specific product.
  /// </summary>
  /// <param name="productId">The product identifier</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The inventory levels if found, null otherwise</returns>
  Task<InventoryLevelDto?> GetByProductIdAsync(Guid productId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets all inventory levels.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of all inventory levels (empty if none found)</returns>
  Task<IReadOnlyList<InventoryLevelDto>> GetAllAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets products with low available inventory.
  /// </summary>
  /// <param name="threshold">Available quantity threshold (default 10)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of inventory levels where Available &lt;= threshold (empty if none found)</returns>
  Task<IReadOnlyList<InventoryLevelDto>> GetLowStockAsync(int threshold = 10, CancellationToken cancellationToken = default);
}
