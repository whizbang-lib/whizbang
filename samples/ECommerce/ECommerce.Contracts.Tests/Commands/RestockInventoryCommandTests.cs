using ECommerce.Contracts.Commands;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Commands;

/// <summary>
/// Tests for RestockInventoryCommand
/// </summary>
public class RestockInventoryCommandTests {
  private static readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();
  [Test]
  public async Task RestockInventoryCommand_WithValidProperties_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var productId = _idProvider.NewGuid();
    var cmd = new RestockInventoryCommand {
      ProductId = productId,
      QuantityToAdd = 100
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo(productId);
    await Assert.That(cmd.QuantityToAdd).IsEqualTo(100);
  }

  [Test]
  public async Task RestockInventoryCommand_WithSmallQuantity_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new RestockInventoryCommand {
      ProductId = _idProvider.NewGuid(),
      QuantityToAdd = 1
    };

    // Assert
    await Assert.That(cmd.QuantityToAdd).IsEqualTo(1);
  }

  [Test]
  public async Task RestockInventoryCommand_WithLargeQuantity_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new RestockInventoryCommand {
      ProductId = _idProvider.NewGuid(),
      QuantityToAdd = 10000
    };

    // Assert
    await Assert.That(cmd.QuantityToAdd).IsEqualTo(10000);
  }

  [Test]
  public async Task RestockInventoryCommand_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var productId = _idProvider.NewGuid();
    var cmd1 = new RestockInventoryCommand {
      ProductId = productId,
      QuantityToAdd = 100
    };

    var cmd2 = new RestockInventoryCommand {
      ProductId = productId,
      QuantityToAdd = 100
    };

    // Act & Assert
    await Assert.That(cmd1).IsEqualTo(cmd2);
  }

  [Test]
  public async Task RestockInventoryCommand_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var productId = _idProvider.NewGuid();
    var cmd = new RestockInventoryCommand {
      ProductId = productId,
      QuantityToAdd = 500
    };

    var stringRep = cmd.ToString();

    // Assert
    await Assert.That(stringRep).Contains(productId.ToString());
    await Assert.That(stringRep).IsNotNull();
  }
}
