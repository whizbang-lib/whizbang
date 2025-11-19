using ECommerce.Contracts.Events;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Events;

/// <summary>
/// Tests for InventoryAdjustedEvent
/// </summary>
public class InventoryAdjustedEventTests {
  [Test]
  public async Task InventoryAdjustedEvent_WithPositiveChange_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var adjustedAt = DateTime.UtcNow;
    var evt = new InventoryAdjustedEvent {
      ProductId = "prod-123",
      QuantityChange = 10,
      NewTotalQuantity = 110,
      Reason = "Inventory correction - found extra units",
      AdjustedAt = adjustedAt
    };

    // Assert
    await Assert.That(evt.ProductId).IsEqualTo("prod-123");
    await Assert.That(evt.QuantityChange).IsEqualTo(10);
    await Assert.That(evt.NewTotalQuantity).IsEqualTo(110);
    await Assert.That(evt.Reason).IsEqualTo("Inventory correction - found extra units");
    await Assert.That(evt.AdjustedAt).IsEqualTo(adjustedAt);
  }

  [Test]
  public async Task InventoryAdjustedEvent_WithNegativeChange_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var evt = new InventoryAdjustedEvent {
      ProductId = "prod-456",
      QuantityChange = -5,
      NewTotalQuantity = 95,
      Reason = "Damaged goods",
      AdjustedAt = DateTime.UtcNow
    };

    // Assert
    await Assert.That(evt.QuantityChange).IsEqualTo(-5);
    await Assert.That(evt.NewTotalQuantity).IsEqualTo(95);
    await Assert.That(evt.Reason).IsEqualTo("Damaged goods");
  }

  [Test]
  public async Task InventoryAdjustedEvent_WithZeroChange_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var evt = new InventoryAdjustedEvent {
      ProductId = "prod-789",
      QuantityChange = 0,
      NewTotalQuantity = 100,
      Reason = "Audit - no discrepancies found",
      AdjustedAt = DateTime.UtcNow
    };

    // Assert
    await Assert.That(evt.QuantityChange).IsEqualTo(0);
    await Assert.That(evt.NewTotalQuantity).IsEqualTo(100);
  }

  [Test]
  public async Task InventoryAdjustedEvent_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var adjustedAt = DateTime.UtcNow;
    var evt1 = new InventoryAdjustedEvent {
      ProductId = "prod-123",
      QuantityChange = -10,
      NewTotalQuantity = 90,
      Reason = "Shrinkage",
      AdjustedAt = adjustedAt
    };

    var evt2 = new InventoryAdjustedEvent {
      ProductId = "prod-123",
      QuantityChange = -10,
      NewTotalQuantity = 90,
      Reason = "Shrinkage",
      AdjustedAt = adjustedAt
    };

    // Act & Assert
    await Assert.That(evt1).IsEqualTo(evt2);
  }

  [Test]
  public async Task InventoryAdjustedEvent_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var evt = new InventoryAdjustedEvent {
      ProductId = "prod-999",
      QuantityChange = -15,
      NewTotalQuantity = 85,
      Reason = "Damaged during shipping",
      AdjustedAt = DateTime.UtcNow
    };

    var stringRep = evt.ToString();

    // Assert
    await Assert.That(stringRep).Contains("prod-999");
    await Assert.That(stringRep).IsNotNull();
  }
}
