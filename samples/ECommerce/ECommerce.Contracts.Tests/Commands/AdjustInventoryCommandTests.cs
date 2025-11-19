using ECommerce.Contracts.Commands;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Commands;

/// <summary>
/// Tests for AdjustInventoryCommand
/// </summary>
public class AdjustInventoryCommandTests {
  [Test]
  public async Task AdjustInventoryCommand_WithPositiveChange_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new AdjustInventoryCommand {
      ProductId = "prod-123",
      QuantityChange = 10,
      Reason = "Inventory correction - found extra units"
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo("prod-123");
    await Assert.That(cmd.QuantityChange).IsEqualTo(10);
    await Assert.That(cmd.Reason).IsEqualTo("Inventory correction - found extra units");
  }

  [Test]
  public async Task AdjustInventoryCommand_WithNegativeChange_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new AdjustInventoryCommand {
      ProductId = "prod-456",
      QuantityChange = -5,
      Reason = "Damaged goods"
    };

    // Assert
    await Assert.That(cmd.QuantityChange).IsEqualTo(-5);
    await Assert.That(cmd.Reason).IsEqualTo("Damaged goods");
  }

  [Test]
  public async Task AdjustInventoryCommand_WithZeroChange_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new AdjustInventoryCommand {
      ProductId = "prod-789",
      QuantityChange = 0,
      Reason = "Audit - no discrepancies found"
    };

    // Assert
    await Assert.That(cmd.QuantityChange).IsEqualTo(0);
    await Assert.That(cmd.Reason).IsEqualTo("Audit - no discrepancies found");
  }

  [Test]
  public async Task AdjustInventoryCommand_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var cmd1 = new AdjustInventoryCommand {
      ProductId = "prod-123",
      QuantityChange = -10,
      Reason = "Shrinkage"
    };

    var cmd2 = new AdjustInventoryCommand {
      ProductId = "prod-123",
      QuantityChange = -10,
      Reason = "Shrinkage"
    };

    // Act & Assert
    await Assert.That(cmd1).IsEqualTo(cmd2);
  }

  [Test]
  public async Task AdjustInventoryCommand_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var cmd = new AdjustInventoryCommand {
      ProductId = "prod-999",
      QuantityChange = -15,
      Reason = "Damaged during shipping"
    };

    var stringRep = cmd.ToString();

    // Assert
    await Assert.That(stringRep).Contains("prod-999");
    await Assert.That(stringRep).IsNotNull();
  }
}
