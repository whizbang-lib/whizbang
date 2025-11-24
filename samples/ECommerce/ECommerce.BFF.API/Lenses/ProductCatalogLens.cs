using System.Data;
using Dapper;
using Whizbang.Core.Data;

namespace ECommerce.BFF.API.Lenses;

/// <summary>
/// Read-only query implementation for product catalog data.
/// Queries the bff.product_catalog table materialized by ProductCatalogPerspective.
/// </summary>
public class ProductCatalogLens : IProductCatalogLens {
  private readonly IDbConnectionFactory _connectionFactory;

  public ProductCatalogLens(IDbConnectionFactory connectionFactory) {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
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
      FROM bff.product_catalog
      WHERE product_id = @ProductId AND deleted_at IS NULL",
      new { ProductId = productId });
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<ProductDto>> GetAllAsync(bool includeDeleted = false, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var sql = @"
      SELECT
        product_id AS ProductId,
        name AS Name,
        description AS Description,
        price AS Price,
        image_url AS ImageUrl,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt,
        deleted_at AS DeletedAt
      FROM bff.product_catalog";

    if (!includeDeleted) {
      sql += " WHERE deleted_at IS NULL";
    }

    var results = await connection.QueryAsync<ProductDto>(sql);
    return results.ToList();
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<ProductDto>> GetByIdsAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    var ids = productIds.ToArray();
    if (ids.Length == 0) {
      return Array.Empty<ProductDto>();
    }

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
      FROM bff.product_catalog
      WHERE product_id = ANY(@ProductIds) AND deleted_at IS NULL",
      new { ProductIds = ids });

    return results.ToList();
  }

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
