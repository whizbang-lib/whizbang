using ECommerce.Contracts.Events;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Events;

/// <summary>
/// Tests for InventoryRestockedEvent
/// </summary>
public class InventoryRestockedEventTests {
  private static readonly IWhizbangIdProvider IdProvider = new Uuid7IdProvider();
  [Test]
  public async Task InventoryRestockedEvent_WithValidProperties_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var productId = IdProvider.NewGuid();
    var restockedAt = DateTime.UtcNow;
    var evt = new InventoryRestockedEvent {
      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 150,
      RestockedAt = restockedAt
    };

    // Assert
    await Assert.That(evt.ProductId).IsEqualTo(productId);
    await Assert.That(evt.QuantityAdded).IsEqualTo(100);
    await Assert.That(evt.NewTotalQuantity).IsEqualTo(150);
    await Assert.That(evt.RestockedAt).IsEqualTo(restockedAt);
  }

  [Test]
  public async Task InventoryRestockedEvent_WithZeroInitialStock_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var evt = new InventoryRestockedEvent {
      ProductId = IdProvider.NewGuid(),
      QuantityAdded = 50,
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    };

    // Assert
    await Assert.That(evt.QuantityAdded).IsEqualTo(50);
    await Assert.That(evt.NewTotalQuantity).IsEqualTo(50);
  }

  [Test]
  public async Task InventoryRestockedEvent_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var productId = IdProvider.NewGuid();
    var restockedAt = DateTime.UtcNow;
    var evt1 = new InventoryRestockedEvent {
      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 150,
      RestockedAt = restockedAt
    };

    var evt2 = new InventoryRestockedEvent {
      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 150,
      RestockedAt = restockedAt
    };

    // Act & Assert
    await Assert.That(evt1).IsEqualTo(evt2);
  }

  [Test]
  public async Task InventoryRestockedEvent_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var productId = IdProvider.NewGuid();
    var evt = new InventoryRestockedEvent {
      ProductId = productId,
      QuantityAdded = 500,
      NewTotalQuantity = 1000,
      RestockedAt = DateTime.UtcNow
    };

    var stringRep = evt.ToString();

    // Assert
    await Assert.That(stringRep).Contains(productId.ToString());
    await Assert.That(stringRep).IsNotNull();
  }

  [Test]
  public async Task InventoryRestockedEvent_WithLargeQuantities_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var evt = new InventoryRestockedEvent {
      ProductId = IdProvider.NewGuid(),
      QuantityAdded = 10000,
      NewTotalQuantity = 25000,
      RestockedAt = DateTime.UtcNow
    };

    // Assert
    await Assert.That(evt.QuantityAdded).IsEqualTo(10000);
    await Assert.That(evt.NewTotalQuantity).IsEqualTo(25000);
  }
}
