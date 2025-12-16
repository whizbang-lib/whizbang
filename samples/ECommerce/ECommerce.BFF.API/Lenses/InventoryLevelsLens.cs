using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace ECommerce.BFF.API.Lenses;

/// <summary>
/// EF Core implementation of IInventoryLevelsLens for fast readonly queries.
/// Uses ILensQuery abstraction with LINQ for type-safe queries - zero reflection, AOT compatible.
/// </summary>
public class InventoryLevelsLens(ILensQuery<InventoryLevelDto> query) : IInventoryLevelsLens {
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
      .Where(row => row.Data.Available <= threshold)
      .Select(row => row.Data)
      .ToListAsync(cancellationToken);

    return results.AsReadOnly();
  }
}
