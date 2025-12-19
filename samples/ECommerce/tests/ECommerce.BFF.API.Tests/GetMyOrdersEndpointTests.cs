using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Commands;

namespace ECommerce.BFF.API.Tests;

/// <summary>
/// Unit tests for GetMyOrdersEndpoint
/// </summary>
public class GetMyOrdersEndpointTests {
  /// <summary>
  /// Mock implementation of IOrderLens for testing
  /// </summary>
  private class MockOrderLens(List<OrderReadModel> orders) : IOrderLens {
    private readonly List<OrderReadModel> _orders = orders;

    public Task<IEnumerable<OrderReadModel>> GetByCustomerIdAsync(string customerId, CancellationToken cancellationToken = default) {
      var result = _orders.Where(o => o.CustomerId.ToString() == customerId);
      return Task.FromResult(result);
    }

    public Task<OrderReadModel?> GetByIdAsync(string orderId, CancellationToken cancellationToken = default) {
      return Task.FromResult(_orders.FirstOrDefault(o => o.OrderId.ToString() == orderId));
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
      new() {
        OrderId = OrderId.Parse("01234567-89ab-7def-0123-456789abcdef"),
        CustomerId = CustomerId.Parse("fedcba98-7654-7210-fedc-ba9876543210"),
        TenantId = "TENANT-001",
        Status = "Pending",
        TotalAmount = 100.00m,
        CreatedAt = DateTime.UtcNow,
        LineItems = []
      },
      new() {
        OrderId = OrderId.Parse("11111111-2222-7333-4444-555555555555"),
        CustomerId = CustomerId.Parse("99999999-8888-7777-6666-555555555555"),
        TenantId = "TENANT-001",
        Status = "Completed",
        TotalAmount = 200.00m,
        CreatedAt = DateTime.UtcNow,
        LineItems = []
      }
    };

    var mockLens = new MockOrderLens(testOrders);

    // Act
    var result = await mockLens.GetByCustomerIdAsync("fedcba98-7654-7210-fedc-ba9876543210");

    // Assert
    await Assert.That(result).HasCount().EqualTo(1);
    var order = result.First();
    await Assert.That(order.OrderId).IsEqualTo(OrderId.Parse("01234567-89ab-7def-0123-456789abcdef"));
    await Assert.That(order.CustomerId).IsEqualTo(CustomerId.Parse("fedcba98-7654-7210-fedc-ba9876543210"));
  }

  [Test]
  public async Task MockOrderLens_GetByCustomerId_WithNoOrders_ReturnsEmptyAsync() {
    // Arrange
    var mockLens = new MockOrderLens([]);

    // Act
    var result = await mockLens.GetByCustomerIdAsync("CUST-999");

    // Assert
    await Assert.That(result).IsEmpty();
  }
}
