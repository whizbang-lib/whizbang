using ECommerce.Contracts.Commands;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Commands;

/// <summary>
/// Tests for AdjustInventoryCommand
/// </summary>
public class AdjustInventoryCommandTests {
  private static readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();
  [Test]
  public async Task AdjustInventoryCommand_WithPositiveChange_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var productId = _idProvider.NewGuid();
    var cmd = new AdjustInventoryCommand {
      ProductId = productId,
      QuantityChange = 10,
      Reason = "Inventory correction - found extra units"
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo(productId);
    await Assert.That(cmd.QuantityChange).IsEqualTo(10);
    await Assert.That(cmd.Reason).IsEqualTo("Inventory correction - found extra units");
  }

  [Test]
  public async Task AdjustInventoryCommand_WithNegativeChange_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new AdjustInventoryCommand {
      ProductId = _idProvider.NewGuid(),
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
      ProductId = _idProvider.NewGuid(),
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
    var productId = _idProvider.NewGuid();
    var cmd1 = new AdjustInventoryCommand {
      ProductId = productId,
      QuantityChange = -10,
      Reason = "Shrinkage"
    };

    var cmd2 = new AdjustInventoryCommand {
      ProductId = productId,
      QuantityChange = -10,
      Reason = "Shrinkage"
    };

    // Act & Assert
    await Assert.That(cmd1).IsEqualTo(cmd2);
  }

  [Test]
  public async Task AdjustInventoryCommand_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var productId = _idProvider.NewGuid();
    var cmd = new AdjustInventoryCommand {
      ProductId = productId,
      QuantityChange = -15,
      Reason = "Damaged during shipping"
    };

    var stringRep = cmd.ToString();

    // Assert
    await Assert.That(stringRep).Contains(productId.ToString());
    await Assert.That(stringRep).IsNotNull();
  }
}
