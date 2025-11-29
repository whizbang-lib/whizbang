using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace ECommerce.BFF.API.Lenses;

/// <summary>
/// EF Core implementation of IOrderLens for fast readonly queries.
/// Uses ILensQuery abstraction with LINQ for type-safe queries - zero reflection, AOT compatible.
/// </summary>
public class OrderLens(ILensQuery<OrderReadModel> query, ILogger<OrderLens> logger) : IOrderLens {
  private readonly ILensQuery<OrderReadModel> _query = query;
  private readonly ILogger<OrderLens> _logger = logger;

  public async Task<OrderReadModel?> GetByIdAsync(string orderId, CancellationToken cancellationToken = default) {
    return await _query.GetByIdAsync(orderId, cancellationToken);
  }

  public async Task<IEnumerable<OrderReadModel>> GetByCustomerIdAsync(string customerId, CancellationToken cancellationToken = default) {
    return await _query.Query
      .Where(row => row.Scope.CustomerId == customerId)
      .OrderByDescending(row => row.CreatedAt)
      .Select(row => row.Data)
      .ToListAsync(cancellationToken);
  }

  public async Task<IEnumerable<OrderReadModel>> GetByTenantIdAsync(string tenantId, CancellationToken cancellationToken = default) {
    return await _query.Query
      .Where(row => row.Scope.TenantId == tenantId)
      .OrderByDescending(row => row.CreatedAt)
      .Select(row => row.Data)
      .ToListAsync(cancellationToken);
  }

  public async Task<IEnumerable<OrderReadModel>> GetRecentOrdersAsync(int limit = 100, CancellationToken cancellationToken = default) {
    return await _query.Query
      .OrderByDescending(row => row.CreatedAt)
      .Take(limit)
      .Select(row => row.Data)
      .ToListAsync(cancellationToken);
  }

  public async Task<IEnumerable<OrderReadModel>> GetByStatusAsync(string tenantId, string status, CancellationToken cancellationToken = default) {
    return await _query.Query
      .Where(row => row.Scope.TenantId == tenantId && row.Data.Status == status)
      .OrderByDescending(row => row.CreatedAt)
      .Select(row => row.Data)
      .ToListAsync(cancellationToken);
  }

  public async Task<IEnumerable<OrderStatusHistory>> GetStatusHistoryAsync(string orderId, CancellationToken cancellationToken = default) {
    // NOTE: With JSONB pattern, we no longer have a separate status history table.
    // The metadata column contains the EventType for each update.
    // For now, return empty list. In the future, could query event store for full history.
    _logger.LogWarning("GetStatusHistoryAsync called but status history table no longer exists with JSONB pattern. Returning empty list.");
    return [];
  }
}
