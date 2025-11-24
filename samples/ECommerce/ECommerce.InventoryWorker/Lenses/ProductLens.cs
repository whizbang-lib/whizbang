using System.Data;
using Dapper;
using Whizbang.Core.Data;

namespace ECommerce.InventoryWorker.Lenses;

/// <summary>
/// Read-only query implementation for product catalog data.
/// Queries the product_catalog table materialized by ProductCatalogPerspective.
/// </summary>
public class ProductLens : IProductLens {
  private readonly IDbConnectionFactory _connectionFactory;

  public ProductLens(IDbConnectionFactory connectionFactory) {
    _connectionFactory = connectionFactory;
  }

  /// <inheritdoc />
  public async Task<ProductDto?> GetByIdAsync(Guid productId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    return await connection.QuerySingleOrDefaultAsync<ProductDto>(@"
      SELECT
        product_id AS ProductId,
        name AS Name,
        description AS Description,
        price AS Price,
        image_url AS ImageUrl,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt,
        deleted_at AS DeletedAt
      FROM inventoryworker.product_catalog
      WHERE product_id = @ProductId AND deleted_at IS NULL",
      new { ProductId = productId });
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<ProductDto>> GetAllAsync(bool includeDeleted = false, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = includeDeleted
      ? @"
        SELECT
          product_id AS ProductId,
          name AS Name,
          description AS Description,
          price AS Price,
          image_url AS ImageUrl,
          created_at AS CreatedAt,
          updated_at AS UpdatedAt,
          deleted_at AS DeletedAt
        FROM inventoryworker.product_catalog"
      : @"
        SELECT
          product_id AS ProductId,
          name AS Name,
          description AS Description,
          price AS Price,
          image_url AS ImageUrl,
          created_at AS CreatedAt,
          updated_at AS UpdatedAt,
          deleted_at AS DeletedAt
        FROM inventoryworker.product_catalog
        WHERE deleted_at IS NULL";

    var results = await connection.QueryAsync<ProductDto>(sql);
    return results.ToList();
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<ProductDto>> GetByIdsAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default) {
    var idList = productIds.ToList();
    if (idList.Count == 0) {
      return Array.Empty<ProductDto>();
    }

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var results = await connection.QueryAsync<ProductDto>(@"
      SELECT
        product_id AS ProductId,
        name AS Name,
        description AS Description,
        price AS Price,
        image_url AS ImageUrl,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt,
        deleted_at AS DeletedAt
      FROM inventoryworker.product_catalog
      WHERE product_id = ANY(@ProductIds) AND deleted_at IS NULL",
      new { ProductIds = idList.ToArray() });

    return results.ToList();
  }

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
