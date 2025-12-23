using ECommerce.BFF.API.Lenses;
using HotChocolate.Data;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace ECommerce.BFF.API.GraphQL;

/// <summary>
/// GraphQL queries for product catalog and inventory.
/// Uses IQueryable projections for efficient filtering, sorting, and paging.
/// </summary>
public class CatalogQueries {
  /// <summary>
  /// Query products with filtering, sorting, and projection support.
  /// Examples:
  /// - Filter by price: products(where: { price: { gt: 50 } })
  /// - Sort by name: products(order: { name: ASC })
  /// - Project fields: products { productId name price }
  /// </summary>
  [UseProjection]
  [UseFiltering]
  [UseSorting]
  public IQueryable<ProductDto> GetProducts(
    [Service] ILensQuery<ProductDto> productQuery) {
    // Return IQueryable for HotChocolate to apply filters/sorts/projections
    // Filter out deleted products by default
    return productQuery.Query
      .AsNoTracking()
      .Where(row => row.Data.DeletedAt == null)
      .Select(row => row.Data);
  }

  /// <summary>
  /// Get a single product by ID.
  /// </summary>
  public async Task<ProductDto?> GetProductAsync(
    Guid productId,
    [Service] IProductCatalogLens productLens,
    CancellationToken cancellationToken) {
    return await productLens.GetByIdAsync(productId, cancellationToken);
  }

  /// <summary>
  /// Query inventory levels with filtering, sorting, and projection support.
  /// Examples:
  /// - Find low stock: inventory(where: { availableQuantity: { lt: 10 } })
  /// - Sort by quantity: inventory(order: { availableQuantity: DESC })
  /// </summary>
  [UseProjection]
  [UseFiltering]
  [UseSorting]
  public IQueryable<InventoryLevelDto> GetInventory(
    [Service] ILensQuery<InventoryLevelDto> inventoryQuery) {
    // Return IQueryable for HotChocolate to apply filters/sorts/projections
    return inventoryQuery.Query
      .AsNoTracking()
      .Select(row => row.Data);
  }

  /// <summary>
  /// Get inventory level for a specific product.
  /// </summary>
  public async Task<InventoryLevelDto?> GetProductInventoryAsync(
    Guid productId,
    [Service] IInventoryLevelsLens inventoryLens,
    CancellationToken cancellationToken) {
    return await inventoryLens.GetByProductIdAsync(productId, cancellationToken);
  }
}
