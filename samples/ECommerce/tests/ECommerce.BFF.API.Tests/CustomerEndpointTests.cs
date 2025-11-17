using ECommerce.BFF.API.Endpoints;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Commands;

namespace ECommerce.BFF.API.Tests;

/// <summary>
/// Unit tests for customer-facing endpoints
/// </summary>
public class CustomerEndpointTests {
  private static List<OrderReadModel> CreateTestOrders() {
    return new List<OrderReadModel> {
      new OrderReadModel {
        OrderId = "ORD-001",
        CustomerId = "CUST-001",
        TenantId = "TENANT-001",
        Status = "Pending",
        TotalAmount = 100.00m,
        CreatedAt = DateTime.UtcNow.AddHours(-2),
        UpdatedAt = DateTime.UtcNow.AddHours(-1),
        ItemCount = 2,
        LineItems = new List<LineItemReadModel> {
          new LineItemReadModel {
            ProductId = "PROD-001",
            ProductName = "Product 1",
            Quantity = 2,
            Price = 50.00m
          }
        }
      },
      new OrderReadModel {
        OrderId = "ORD-002",
        CustomerId = "CUST-001",
        TenantId = "TENANT-001",
        Status = "Completed",
        TotalAmount = 200.00m,
        CreatedAt = DateTime.UtcNow.AddHours(-5),
        UpdatedAt = DateTime.UtcNow.AddHours(-3),
        ItemCount = 1,
        LineItems = new List<LineItemReadModel> {
          new LineItemReadModel {
            ProductId = "PROD-002",
            ProductName = "Product 2",
            Quantity = 1,
            Price = 200.00m
          }
        }
      },
      new OrderReadModel {
        OrderId = "ORD-003",
        CustomerId = "CUST-002",
        TenantId = "TENANT-001",
        Status = "Pending",
        TotalAmount = 150.00m,
        CreatedAt = DateTime.UtcNow.AddHours(-1),
        UpdatedAt = DateTime.UtcNow,
        ItemCount = 3,
        LineItems = new List<LineItemReadModel>()
      }
    };
  }

  [Test]
  public async Task GetMyOrdersEndpoint_CanBeConstructedAsync() {
    // Arrange & Act
    var mockLens = new MockOrderLens(new List<OrderReadModel>());
    var endpoint = new GetMyOrdersEndpoint(mockLens);

    // Assert
    await Assert.That(endpoint).IsNotNull();
  }

  [Test]
  public async Task GetOrderByIdEndpoint_CanBeConstructedAsync() {
    // Arrange & Act
    var mockLens = new MockOrderLens(new List<OrderReadModel>());
    var endpoint = new GetOrderByIdEndpoint(mockLens);

    // Assert
    await Assert.That(endpoint).IsNotNull();
  }

  [Test]
  public async Task GetOrderHistoryEndpoint_CanBeConstructedAsync() {
    // Arrange & Act
    var mockLens = new MockOrderLens(new List<OrderReadModel>());
    var endpoint = new GetOrderHistoryEndpoint(mockLens);

    // Assert
    await Assert.That(endpoint).IsNotNull();
  }

  [Test]
  public async Task CreateOrderEndpoint_CanBeConstructedAsync() {
    // Arrange & Act
    var mockLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<CreateOrderEndpoint>.Instance;
    var endpoint = new CreateOrderEndpoint(mockLogger);

    // Assert
    await Assert.That(endpoint).IsNotNull();
  }

  [Test]
  public async Task CreateOrderRequest_WithValidData_CreatesValidRequestAsync() {
    // Arrange & Act
    var request = new CreateOrderRequest {
      CustomerId = "CUST-001",
      LineItems = new List<CreateOrderLineItem> {
        new CreateOrderLineItem {
          ProductId = "PROD-001",
          ProductName = "Test Product",
          Quantity = 2,
          Price = 50.00m
        }
      }
    };

    // Assert
    await Assert.That(request.CustomerId).IsEqualTo("CUST-001");
    await Assert.That(request.LineItems).HasCount().EqualTo(1);
    await Assert.That(request.LineItems[0].ProductId).IsEqualTo("PROD-001");
    await Assert.That(request.LineItems[0].Quantity).IsEqualTo(2);
    await Assert.That(request.LineItems[0].Price).IsEqualTo(50.00m);
  }

  [Test]
  public async Task CreateOrderResponse_WithValidData_CreatesValidResponseAsync() {
    // Arrange & Act
    var response = new CreateOrderResponse {
      CorrelationId = "COR-123",
      Message = "Order is being processed"
    };

    // Assert
    await Assert.That(response.CorrelationId).IsEqualTo("COR-123");
    await Assert.That(response.Message).IsEqualTo("Order is being processed");
  }

  [Test]
  public async Task CreateOrderLineItem_WithValidData_CreatesCorrectlyAsync() {
    // Arrange & Act
    var lineItem = new CreateOrderLineItem {
      ProductId = "PROD-999",
      ProductName = "Amazing Product",
      Quantity = 3,
      Price = 25.99m
    };

    // Assert
    await Assert.That(lineItem.ProductId).IsEqualTo("PROD-999");
    await Assert.That(lineItem.ProductName).IsEqualTo("Amazing Product");
    await Assert.That(lineItem.Quantity).IsEqualTo(3);
    await Assert.That(lineItem.Price).IsEqualTo(25.99m);
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
