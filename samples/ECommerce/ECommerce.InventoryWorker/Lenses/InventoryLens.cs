using System.Data;
using Dapper;
using Whizbang.Core.Data;

namespace ECommerce.InventoryWorker.Lenses;

/// <summary>
/// Read-only query implementation for inventory level data.
/// Queries the inventory_levels table materialized by InventoryLevelsPerspective.
/// </summary>
public class InventoryLens(IDbConnectionFactory connectionFactory) : IInventoryLens {
  private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

  /// <inheritdoc />
  public async Task<InventoryLevelDto?> GetByProductIdAsync(Guid productId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    return await connection.QuerySingleOrDefaultAsync<InventoryLevelDto>(@"
      SELECT
        product_id AS ProductId,
        quantity AS Quantity,
        reserved AS Reserved,
        available AS Available,
        last_updated AS LastUpdated
      FROM inventoryworker.inventory_levels
      WHERE product_id = @ProductId",
      new { ProductId = productId });
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<InventoryLevelDto>> GetAllAsync(CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var results = await connection.QueryAsync<InventoryLevelDto>(@"
      SELECT
        product_id AS ProductId,
        quantity AS Quantity,
        reserved AS Reserved,
        available AS Available,
        last_updated AS LastUpdated
      FROM inventoryworker.inventory_levels");

    return [.. results];
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<InventoryLevelDto>> GetLowStockAsync(int threshold = 10, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var results = await connection.QueryAsync<InventoryLevelDto>(@"
      SELECT
        product_id AS ProductId,
        quantity AS Quantity,
        reserved AS Reserved,
        available AS Available,
        last_updated AS LastUpdated
      FROM inventoryworker.inventory_levels
      WHERE available <= @Threshold",
      new { Threshold = threshold });

    return [.. results];
  }

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
