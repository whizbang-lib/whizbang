namespace ECommerce.BFF.API.Lenses;

/// <summary>
/// Read-only query interface for product catalog data.
/// Queries the bff.product_catalog table materialized by ProductCatalogPerspective.
/// </summary>
public interface IProductCatalogLens {
  /// <summary>
  /// Gets a single product by ID.
  /// </summary>
  /// <param name="productId">The product identifier</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The product DTO if found and not deleted, null otherwise</returns>
  Task<ProductDto?> GetByIdAsync(Guid productId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets all products.
  /// </summary>
  /// <param name="includeDeleted">Whether to include soft-deleted products</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of products (empty if none found)</returns>
  Task<IReadOnlyList<ProductDto>> GetAllAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets products by a list of IDs.
  /// </summary>
  /// <param name="productIds">Product identifiers to retrieve</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of matching products (only non-deleted, empty if none found)</returns>
  Task<IReadOnlyList<ProductDto>> GetByIdsAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default);
}
