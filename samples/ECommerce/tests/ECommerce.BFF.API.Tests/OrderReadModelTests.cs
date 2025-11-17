using ECommerce.BFF.API.Lenses;

namespace ECommerce.BFF.API.Tests;

/// <summary>
/// Unit tests for read models
/// </summary>
public class OrderReadModelTests {
  [Test]
  public async Task OrderReadModel_WithValidData_CreatesCorrectlyAsync() {
    // Arrange
    var createdAt = DateTime.UtcNow;
    var updatedAt = DateTime.UtcNow.AddMinutes(5);

    // Act
    var order = new OrderReadModel {
      OrderId = "ORD-001",
      CustomerId = "CUST-001",
      TenantId = "TENANT-001",
      Status = "Pending",
      TotalAmount = 150.00m,
      CreatedAt = createdAt,
      UpdatedAt = updatedAt,
      ItemCount = 3,
      PaymentStatus = "Pending",
      ShipmentId = "SHIP-001",
      TrackingNumber = "TRACK-123",
      LineItems = new List<LineItemReadModel> {
        new LineItemReadModel {
          ProductId = "PROD-001",
          ProductName = "Product 1",
          Quantity = 2,
          Price = 50.00m
        },
        new LineItemReadModel {
          ProductId = "PROD-002",
          ProductName = "Product 2",
          Quantity = 1,
          Price = 50.00m
        }
      }
    };

    // Assert
    await Assert.That(order.OrderId).IsEqualTo("ORD-001");
    await Assert.That(order.CustomerId).IsEqualTo("CUST-001");
    await Assert.That(order.TenantId).IsEqualTo("TENANT-001");
    await Assert.That(order.Status).IsEqualTo("Pending");
    await Assert.That(order.TotalAmount).IsEqualTo(150.00m);
    await Assert.That(order.CreatedAt).IsEqualTo(createdAt);
    await Assert.That(order.UpdatedAt).IsEqualTo(updatedAt);
    await Assert.That(order.ItemCount).IsEqualTo(3);
    await Assert.That(order.PaymentStatus).IsEqualTo("Pending");
    await Assert.That(order.ShipmentId).IsEqualTo("SHIP-001");
    await Assert.That(order.TrackingNumber).IsEqualTo("TRACK-123");
    await Assert.That(order.LineItems).HasCount().EqualTo(2);
  }

  [Test]
  public async Task OrderReadModel_WithNullOptionalFields_HandlesCorrectlyAsync() {
    // Arrange & Act
    var order = new OrderReadModel {
      OrderId = "ORD-002",
      CustomerId = "CUST-002",
      TenantId = null,
      Status = "Created",
      TotalAmount = 50.00m,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      ItemCount = 1,
      PaymentStatus = null,
      ShipmentId = null,
      TrackingNumber = null,
      LineItems = new List<LineItemReadModel>()
    };

    // Assert
    await Assert.That(order.TenantId).IsNull();
    await Assert.That(order.PaymentStatus).IsNull();
    await Assert.That(order.ShipmentId).IsNull();
    await Assert.That(order.TrackingNumber).IsNull();
    await Assert.That(order.LineItems).IsEmpty();
  }

  [Test]
  public async Task LineItemReadModel_WithValidData_CreatesCorrectlyAsync() {
    // Arrange & Act
    var lineItem = new LineItemReadModel {
      ProductId = "PROD-123",
      ProductName = "Test Product",
      Quantity = 5,
      Price = 29.99m
    };

    // Assert
    await Assert.That(lineItem.ProductId).IsEqualTo("PROD-123");
    await Assert.That(lineItem.ProductName).IsEqualTo("Test Product");
    await Assert.That(lineItem.Quantity).IsEqualTo(5);
    await Assert.That(lineItem.Price).IsEqualTo(29.99m);
  }

  [Test]
  public async Task LineItemReadModel_CalculatesTotalPrice_CorrectlyAsync() {
    // Arrange
    var lineItem = new LineItemReadModel {
      ProductId = "PROD-001",
      ProductName = "Widget",
      Quantity = 10,
      Price = 15.50m
    };

    // Act
    var totalPrice = lineItem.Quantity * lineItem.Price;

    // Assert
    await Assert.That(totalPrice).IsEqualTo(155.00m);
  }

  [Test]
  public async Task OrderStatusHistory_WithValidData_CreatesCorrectlyAsync() {
    // Arrange
    var timestamp = DateTime.UtcNow;

    // Act
    var history = new OrderStatusHistory {
      Id = 1,
      OrderId = "ORD-001",
      Status = "Created",
      EventType = "OrderCreatedEvent",
      Timestamp = timestamp,
      Details = "{\"totalAmount\":100.00,\"itemCount\":2}"
    };

    // Assert
    await Assert.That(history.Id).IsEqualTo(1);
    await Assert.That(history.OrderId).IsEqualTo("ORD-001");
    await Assert.That(history.Status).IsEqualTo("Created");
    await Assert.That(history.EventType).IsEqualTo("OrderCreatedEvent");
    await Assert.That(history.Timestamp).IsEqualTo(timestamp);
    await Assert.That(history.Details).IsNotNull();
  }

  [Test]
  public async Task OrderStatusHistory_WithNullDetails_HandlesCorrectlyAsync() {
    // Arrange & Act
    var history = new OrderStatusHistory {
      Id = 2,
      OrderId = "ORD-002",
      Status = "Cancelled",
      EventType = "OrderCancelledEvent",
      Timestamp = DateTime.UtcNow,
      Details = null
    };

    // Assert
    await Assert.That(history.Details).IsNull();
  }

  [Test]
  public async Task OrderReadModel_DifferentOrders_AreNotEqualAsync() {
    // Arrange
    var createdAt = DateTime.UtcNow;
    var order1 = new OrderReadModel {
      OrderId = "ORD-001",
      CustomerId = "CUST-001",
      TenantId = "TENANT-001",
      Status = "Pending",
      TotalAmount = 100.00m,
      CreatedAt = createdAt,
      UpdatedAt = createdAt,
      ItemCount = 2,
      LineItems = new List<LineItemReadModel>()
    };

    var order2 = new OrderReadModel {
      OrderId = "ORD-002",
      CustomerId = "CUST-002",
      TenantId = "TENANT-001",
      Status = "Completed",
      TotalAmount = 200.00m,
      CreatedAt = createdAt,
      UpdatedAt = createdAt,
      ItemCount = 1,
      LineItems = new List<LineItemReadModel>()
    };

    // Assert
    await Assert.That(order1).IsNotEqualTo(order2);
    await Assert.That(order1.OrderId).IsNotEqualTo(order2.OrderId);
    await Assert.That(order1.CustomerId).IsNotEqualTo(order2.CustomerId);
    await Assert.That(order1.Status).IsNotEqualTo(order2.Status);
  }
}
