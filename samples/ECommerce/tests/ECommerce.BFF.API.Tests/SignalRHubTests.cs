using ECommerce.BFF.API.Hubs;

namespace ECommerce.BFF.API.Tests;

/// <summary>
/// Unit tests for SignalR hub types and notifications
/// </summary>
public class SignalRHubTests {
  [Test]
  public async Task OrderStatusUpdate_WithValidData_CreatesCorrectlyAsync() {
    // Arrange
    var timestamp = DateTime.UtcNow;

    // Act
    var update = new OrderStatusUpdate {
      OrderId = "ORD-001",
      Status = "Processing",
      Timestamp = timestamp,
      Message = "Order is being processed",
      Details = new Dictionary<string, object> {
        { "warehouse", "US-WEST-1" },
        { "priority", "high" }
      }
    };

    // Assert
    await Assert.That(update.OrderId).IsEqualTo("ORD-001");
    await Assert.That(update.Status).IsEqualTo("Processing");
    await Assert.That(update.Timestamp).IsEqualTo(timestamp);
    await Assert.That(update.Message).IsEqualTo("Order is being processed");
    await Assert.That(update.Details).IsNotNull();
    await Assert.That(update.Details!).HasCount().EqualTo(2);
    await Assert.That(update.Details["warehouse"]).IsEqualTo("US-WEST-1");
    await Assert.That(update.Details["priority"]).IsEqualTo("high");
  }

  [Test]
  public async Task OrderStatusUpdate_WithoutOptionalFields_HandlesCorrectlyAsync() {
    // Arrange & Act
    var update = new OrderStatusUpdate {
      OrderId = "ORD-002",
      Status = "Shipped",
      Message = null,
      Details = null
    };

    // Assert
    await Assert.That(update.OrderId).IsEqualTo("ORD-002");
    await Assert.That(update.Status).IsEqualTo("Shipped");
    await Assert.That(update.Message).IsNull();
    await Assert.That(update.Details).IsNull();
  }

  [Test]
  public async Task OrderCreatedNotification_WithValidData_CreatesCorrectlyAsync() {
    // Arrange
    var createdAt = DateTime.UtcNow;

    // Act
    var notification = new OrderCreatedNotification {
      OrderId = "ORD-003",
      CustomerId = "CUST-001",
      TotalAmount = 299.99m,
      CreatedAt = createdAt
    };

    // Assert
    await Assert.That(notification.OrderId).IsEqualTo("ORD-003");
    await Assert.That(notification.CustomerId).IsEqualTo("CUST-001");
    await Assert.That(notification.TotalAmount).IsEqualTo(299.99m);
    await Assert.That(notification.CreatedAt).IsEqualTo(createdAt);
  }

  [Test]
  public async Task OrderUpdateNotification_WithValidData_CreatesCorrectlyAsync() {
    // Arrange
    var timestamp = DateTime.UtcNow;

    // Act
    var notification = new OrderUpdateNotification {
      OrderId = "ORD-004",
      UpdateType = "PaymentProcessed",
      Timestamp = timestamp,
      Data = new Dictionary<string, object> {
        { "transactionId", "TXN-123456" },
        { "amount", 149.99m }
      }
    };

    // Assert
    await Assert.That(notification.OrderId).IsEqualTo("ORD-004");
    await Assert.That(notification.UpdateType).IsEqualTo("PaymentProcessed");
    await Assert.That(notification.Timestamp).IsEqualTo(timestamp);
    await Assert.That(notification.Data).IsNotNull();
    await Assert.That(notification.Data!).HasCount().EqualTo(2);
  }

  [Test]
  public async Task OrderUpdateNotification_WithNullData_HandlesCorrectlyAsync() {
    // Arrange & Act
    var notification = new OrderUpdateNotification {
      OrderId = "ORD-005",
      UpdateType = "StatusChanged",
      Data = null
    };

    // Assert
    await Assert.That(notification.Data).IsNull();
  }

  [Test]
  public async Task OrderStatusUpdate_DefaultTimestamp_IsSetAsync() {
    // Arrange & Act
    var update = new OrderStatusUpdate {
      OrderId = "ORD-006",
      Status = "Created"
    };

    // Assert - Timestamp should be set to approximately UtcNow
    var now = DateTime.UtcNow;
    await Assert.That(update.Timestamp).IsGreaterThanOrEqualTo(now.AddSeconds(-1));
    await Assert.That(update.Timestamp).IsLessThanOrEqualTo(now.AddSeconds(1));
  }

  [Test]
  public async Task OrderUpdateNotification_DefaultTimestamp_IsSetAsync() {
    // Arrange & Act
    var notification = new OrderUpdateNotification {
      OrderId = "ORD-007",
      UpdateType = "Test"
    };

    // Assert - Timestamp should be set to approximately UtcNow
    var now = DateTime.UtcNow;
    await Assert.That(notification.Timestamp).IsGreaterThanOrEqualTo(now.AddSeconds(-1));
    await Assert.That(notification.Timestamp).IsLessThanOrEqualTo(now.AddSeconds(1));
  }

  [Test]
  public async Task OrderStatusUpdate_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var timestamp = DateTime.UtcNow;
    var update1 = new OrderStatusUpdate {
      OrderId = "ORD-001",
      Status = "Pending",
      Timestamp = timestamp,
      Message = "Test"
    };

    var update2 = new OrderStatusUpdate {
      OrderId = "ORD-001",
      Status = "Pending",
      Timestamp = timestamp,
      Message = "Test"
    };

    var update3 = new OrderStatusUpdate {
      OrderId = "ORD-002",
      Status = "Completed",
      Timestamp = timestamp
    };

    // Assert
    await Assert.That(update1).IsEqualTo(update2);
    await Assert.That(update1).IsNotEqualTo(update3);
  }
}
