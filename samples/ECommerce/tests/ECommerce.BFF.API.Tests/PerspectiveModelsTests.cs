using ECommerce.BFF.API.Perspectives;

namespace ECommerce.BFF.API.Tests;

/// <summary>
/// Unit tests for perspective model records
/// </summary>
public class PerspectiveModelsTests {
  [Test]
  public async Task OrderCreatedDetails_WithValidData_CreatesCorrectlyAsync() {
    // Arrange & Act
    var details = new OrderCreatedDetails {
      TotalAmount = 150.50m,
      ItemCount = 3
    };

    // Assert
    await Assert.That(details.TotalAmount).IsEqualTo(150.50m);
    await Assert.That(details.ItemCount).IsEqualTo(3);
  }

  [Test]
  public async Task InventoryReservedDetails_WithValidData_CreatesCorrectlyAsync() {
    // Arrange
    var reservedAt = DateTime.UtcNow;

    // Act
    var details = new InventoryReservedDetails {
      ProductId = "PROD-001",
      Quantity = 5,
      ReservedAt = reservedAt
    };

    // Assert
    await Assert.That(details.ProductId).IsEqualTo("PROD-001");
    await Assert.That(details.Quantity).IsEqualTo(5);
    await Assert.That(details.ReservedAt).IsEqualTo(reservedAt);
  }

  [Test]
  public async Task PaymentProcessedDetails_WithValidData_CreatesCorrectlyAsync() {
    // Arrange & Act
    var details = new PaymentProcessedDetails {
      Amount = 99.99m,
      TransactionId = "TXN-123456"
    };

    // Assert
    await Assert.That(details.Amount).IsEqualTo(99.99m);
    await Assert.That(details.TransactionId).IsEqualTo("TXN-123456");
  }

  [Test]
  public async Task OrderShippedDetails_WithValidData_CreatesCorrectlyAsync() {
    // Arrange & Act
    var details = new OrderShippedDetails {
      ShipmentId = "SHIP-001",
      TrackingNumber = "TRACK-123456"
    };

    // Assert
    await Assert.That(details.ShipmentId).IsEqualTo("SHIP-001");
    await Assert.That(details.TrackingNumber).IsEqualTo("TRACK-123456");
  }

  [Test]
  public async Task PaymentFailedDetails_WithValidData_CreatesCorrectlyAsync() {
    // Arrange & Act
    var details = new PaymentFailedDetails {
      Reason = "Insufficient funds"
    };

    // Assert
    await Assert.That(details.Reason).IsEqualTo("Insufficient funds");
  }

  [Test]
  public async Task OrderCreatedDetails_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var details1 = new OrderCreatedDetails {
      TotalAmount = 100.00m,
      ItemCount = 2
    };

    var details2 = new OrderCreatedDetails {
      TotalAmount = 100.00m,
      ItemCount = 2
    };

    var details3 = new OrderCreatedDetails {
      TotalAmount = 150.00m,
      ItemCount = 3
    };

    // Assert
    await Assert.That(details1).IsEqualTo(details2);
    await Assert.That(details1).IsNotEqualTo(details3);
  }

  [Test]
  public async Task PaymentProcessedDetails_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var details1 = new PaymentProcessedDetails {
      Amount = 100.00m,
      TransactionId = "TXN-001"
    };

    var details2 = new PaymentProcessedDetails {
      Amount = 100.00m,
      TransactionId = "TXN-001"
    };

    var details3 = new PaymentProcessedDetails {
      Amount = 150.00m,
      TransactionId = "TXN-002"
    };

    // Assert
    await Assert.That(details1).IsEqualTo(details2);
    await Assert.That(details1).IsNotEqualTo(details3);
  }
}
