using ECommerce.BFF.API.Endpoints.SuperAdmin;
using ECommerce.BFF.API.Lenses;

namespace ECommerce.BFF.API.Tests;

/// <summary>
/// Unit tests for super admin endpoints
/// </summary>
public class SuperAdminEndpointTests {
  [Test]
  public async Task GetRecentOrdersEndpoint_CanBeConstructedAsync() {
    // Arrange & Act
    var mockLens = new MockOrderLens(new List<OrderReadModel>());
    var endpoint = new GetRecentOrdersEndpoint(mockLens);

    // Assert
    await Assert.That(endpoint).IsNotNull();
  }

  [Test]
  public async Task GetOrdersByTenantEndpoint_CanBeConstructedAsync() {
    // Arrange & Act
    var mockLens = new MockOrderLens(new List<OrderReadModel>());
    var endpoint = new GetOrdersByTenantEndpoint(mockLens);

    // Assert
    await Assert.That(endpoint).IsNotNull();
  }

  [Test]
  public async Task SuperAdmin_GetOrderByIdEndpoint_CanBeConstructedAsync() {
    // Arrange & Act
    var mockLens = new MockOrderLens(new List<OrderReadModel>());
    var endpoint = new GetOrderByIdEndpoint(mockLens);

    // Assert
    await Assert.That(endpoint).IsNotNull();
  }

  /// <summary>
  /// Mock implementation of IOrderLens for testing
  /// </summary>
  private class MockOrderLens : IOrderLens {
    private readonly List<OrderReadModel> _orders;

    public MockOrderLens(List<OrderReadModel> orders) {
      _orders = orders;
    }

    public Task<IEnumerable<OrderReadModel>> GetByCustomerIdAsync(string customerId, CancellationToken cancellationToken = default) {
      var result = _orders.Where(o => o.CustomerId == customerId);
      return Task.FromResult(result);
    }

    public Task<OrderReadModel?> GetByIdAsync(string orderId, CancellationToken cancellationToken = default) {
      return Task.FromResult(_orders.FirstOrDefault(o => o.OrderId == orderId));
    }

    public Task<IEnumerable<OrderReadModel>> GetByTenantIdAsync(string tenantId, CancellationToken cancellationToken = default) {
      var result = _orders.Where(o => o.TenantId == tenantId);
      return Task.FromResult(result);
    }

    public Task<IEnumerable<OrderReadModel>> GetByStatusAsync(string tenantId, string status, CancellationToken cancellationToken = default) {
      var result = _orders.Where(o => o.TenantId == tenantId && o.Status == status);
      return Task.FromResult(result);
    }

    public Task<IEnumerable<OrderStatusHistory>> GetStatusHistoryAsync(string orderId, CancellationToken cancellationToken = default) {
      return Task.FromResult(Enumerable.Empty<OrderStatusHistory>());
    }

    public Task<IEnumerable<OrderReadModel>> GetRecentOrdersAsync(int limit = 100, CancellationToken cancellationToken = default) {
      return Task.FromResult(_orders.Take(limit).AsEnumerable());
    }
  }
}
