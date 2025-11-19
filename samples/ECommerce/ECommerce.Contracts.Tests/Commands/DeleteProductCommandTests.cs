using ECommerce.Contracts.Commands;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Commands;

/// <summary>
/// Tests for DeleteProductCommand
/// </summary>
public class DeleteProductCommandTests {
  [Test]
  public async Task DeleteProductCommand_WithValidProductId_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new DeleteProductCommand {
      ProductId = "prod-123"
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo("prod-123");
  }

  [Test]
  public async Task DeleteProductCommand_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var cmd1 = new DeleteProductCommand {
      ProductId = "prod-123"
    };

    var cmd2 = new DeleteProductCommand {
      ProductId = "prod-123"
    };

    // Act & Assert
    await Assert.That(cmd1).IsEqualTo(cmd2);
  }

  [Test]
  public async Task DeleteProductCommand_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var cmd = new DeleteProductCommand {
      ProductId = "prod-999"
    };

    var stringRep = cmd.ToString();

    // Assert
    await Assert.That(stringRep).Contains("prod-999");
    await Assert.That(stringRep).IsNotNull();
  }

  [Test]
  public async Task DeleteProductCommand_WithDifferentProductIds_AreNotEqualAsync() {
    // Arrange
    var cmd1 = new DeleteProductCommand {
      ProductId = "prod-123"
    };

    var cmd2 = new DeleteProductCommand {
      ProductId = "prod-456"
    };

    // Act & Assert
    await Assert.That(cmd1).IsNotEqualTo(cmd2);
  }
}
