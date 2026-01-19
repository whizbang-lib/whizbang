using ECommerce.Contracts.Lenses;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace ECommerce.InventoryWorker.Lenses;

/// <summary>
/// EF Core implementation of IInventoryLens for fast readonly queries.
/// Uses ILensQuery abstraction with LINQ for type-safe queries - zero reflection, AOT compatible.
/// </summary>
public class InventoryLens(ILensQuery<InventoryLevelDto> query) : IInventoryLens {
  private readonly ILensQuery<InventoryLevelDto> _query = query;

  /// <inheritdoc />
  public async Task<InventoryLevelDto?> GetByProductIdAsync(Guid productId, CancellationToken cancellationToken = default) {
    return await _query.GetByIdAsync(productId, cancellationToken);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<InventoryLevelDto>> GetAllAsync(CancellationToken cancellationToken = default) {
    var results = await _query.Query
      .AsNoTracking()
      .Select(row => row.Data)
      .ToListAsync(cancellationToken);

    return results.AsReadOnly();
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<InventoryLevelDto>> GetLowStockAsync(int threshold = 10, CancellationToken cancellationToken = default) {
    var results = await _query.Query
      .AsNoTracking()
      .Where(row => row.Data.Quantity - row.Data.Reserved <= threshold)
      .Select(row => row.Data)
      .ToListAsync(cancellationToken);

    return results.AsReadOnly();
  }
}
