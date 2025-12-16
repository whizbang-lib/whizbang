using ECommerce.InventoryWorker.Lenses;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace ECommerce.InventoryWorker.Lenses;

/// <summary>
/// EF Core implementation of IProductLens for fast readonly queries.
/// Uses ILensQuery abstraction with LINQ for type-safe queries - zero reflection, AOT compatible.
/// </summary>
public class ProductLens(ILensQuery<ProductDto> query) : IProductLens {
  private readonly ILensQuery<ProductDto> _query = query;

  /// <inheritdoc />
  public async Task<ProductDto?> GetByIdAsync(Guid productId, CancellationToken cancellationToken = default) {
    var product = await _query.GetByIdAsync(productId, cancellationToken);

    // Filter out deleted products
    if (product?.DeletedAt != null) {
      return null;
    }

    return product;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<ProductDto>> GetAllAsync(bool includeDeleted = false, CancellationToken cancellationToken = default) {
    var query = _query.Query.AsNoTracking();

    if (!includeDeleted) {
      query = query.Where(row => row.Data.DeletedAt == null);
    }

    var results = await query
      .Select(row => row.Data)
      .ToListAsync(cancellationToken);

    return results.AsReadOnly();
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<ProductDto>> GetByIdsAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default) {
    var ids = productIds.ToList();
    if (ids.Count == 0) {
      return Array.Empty<ProductDto>();
    }

    var results = await _query.Query
      .AsNoTracking()
      .Where(row => ids.Contains(row.Id) && row.Data.DeletedAt == null)
      .Select(row => row.Data)
      .ToListAsync(cancellationToken);

    return results.AsReadOnly();
  }
}
