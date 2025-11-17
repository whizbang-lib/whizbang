using ECommerce.BFF.API.Lenses;

namespace ECommerce.BFF.API.Tests;

/// <summary>
/// Unit tests for GetMyOrdersEndpoint
/// </summary>
public class GetMyOrdersEndpointTests {
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


  [Test]
  public async Task MockOrderLens_GetByCustomerId_ReturnsCorrectOrdersAsync() {
    // Arrange
    var testOrders = new List<OrderReadModel> {
      new OrderReadModel {
        OrderId = "ORD-001",
        CustomerId = "CUST-001",
        TenantId = "TENANT-001",
        Status = "Pending",
        TotalAmount = 100.00m,
        CreatedAt = DateTime.UtcNow,
        LineItems = new List<LineItemReadModel>()
      },
      new OrderReadModel {
        OrderId = "ORD-002",
        CustomerId = "CUST-002",
        TenantId = "TENANT-001",
        Status = "Completed",
        TotalAmount = 200.00m,
        CreatedAt = DateTime.UtcNow,
        LineItems = new List<LineItemReadModel>()
      }
    };

    var mockLens = new MockOrderLens(testOrders);

    // Act
    var result = await mockLens.GetByCustomerIdAsync("CUST-001");

    // Assert
    await Assert.That(result).HasCount().EqualTo(1);
    var order = result.First();
    await Assert.That(order.OrderId).IsEqualTo("ORD-001");
    await Assert.That(order.CustomerId).IsEqualTo("CUST-001");
  }

  [Test]
  public async Task MockOrderLens_GetByCustomerId_WithNoOrders_ReturnsEmptyAsync() {
    // Arrange
    var mockLens = new MockOrderLens(new List<OrderReadModel>());

    // Act
    var result = await mockLens.GetByCustomerIdAsync("CUST-999");

    // Assert
    await Assert.That(result).IsEmpty();
  }
}
