using ECommerce.Contracts.Commands;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Commands;

/// <summary>
/// Tests for DeleteProductCommand
/// </summary>
public class DeleteProductCommandTests {
  private static readonly IWhizbangIdProvider IdProvider = new Uuid7IdProvider();
  [Test]
  public async Task DeleteProductCommand_WithValidProductId_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var productId = IdProvider.NewGuid();
    var cmd = new DeleteProductCommand {
      ProductId = productId
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo(productId);
  }

  [Test]
  public async Task DeleteProductCommand_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var productId = IdProvider.NewGuid();
    var cmd1 = new DeleteProductCommand {
      ProductId = productId
    };

    var cmd2 = new DeleteProductCommand {
      ProductId = productId
    };

    // Act & Assert
    await Assert.That(cmd1).IsEqualTo(cmd2);
  }

  [Test]
  public async Task DeleteProductCommand_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var productId = IdProvider.NewGuid();
    var cmd = new DeleteProductCommand {
      ProductId = productId
    };

    var stringRep = cmd.ToString();

    // Assert
    await Assert.That(stringRep).Contains(productId.ToString());
    await Assert.That(stringRep).IsNotNull();
  }

  [Test]
  public async Task DeleteProductCommand_WithDifferentProductIds_AreNotEqualAsync() {
    // Arrange
    var cmd1 = new DeleteProductCommand {
      ProductId = IdProvider.NewGuid()
    };

    var cmd2 = new DeleteProductCommand {
      ProductId = IdProvider.NewGuid()
    };

    // Act & Assert
    await Assert.That(cmd1).IsNotEqualTo(cmd2);
  }
}
