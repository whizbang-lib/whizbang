using ECommerce.Contracts.Commands;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Commands;

/// <summary>
/// Tests for UpdateProductCommand
/// </summary>
public class UpdateProductCommandTests {
  private static readonly IWhizbangIdProvider IdProvider = new Uuid7IdProvider();
  [Test]
  public async Task UpdateProductCommand_WithAllPropertiesUpdated_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var productId = IdProvider.NewGuid();
    var cmd = new UpdateProductCommand {
      ProductId = productId,
      Name = "Updated Widget",
      Description = "Updated description",
      Price = 39.99m,
      ImageUrl = "https://example.com/new-widget.jpg"
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo(productId);
    await Assert.That(cmd.Name).IsEqualTo("Updated Widget");
    await Assert.That(cmd.Description).IsEqualTo("Updated description");
    await Assert.That(cmd.Price).IsEqualTo(39.99m);
    await Assert.That(cmd.ImageUrl).IsEqualTo("https://example.com/new-widget.jpg");
  }

  [Test]
  public async Task UpdateProductCommand_WithOnlyNameUpdated_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var productId = IdProvider.NewGuid();
    var cmd = new UpdateProductCommand {
      ProductId = productId,
      Name = "New Name",
      Description = null,
      Price = null,
      ImageUrl = null
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo(productId);
    await Assert.That(cmd.Name).IsEqualTo("New Name");
    await Assert.That(cmd.Description).IsNull();
    await Assert.That(cmd.Price).IsNull();
    await Assert.That(cmd.ImageUrl).IsNull();
  }

  [Test]
  public async Task UpdateProductCommand_WithAllPropertiesNull_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new UpdateProductCommand {
      ProductId = IdProvider.NewGuid(),
      Name = null,
      Description = null,
      Price = null,
      ImageUrl = null
    };

    // Assert
    await Assert.That(cmd.Name).IsNull();
    await Assert.That(cmd.Description).IsNull();
    await Assert.That(cmd.Price).IsNull();
    await Assert.That(cmd.ImageUrl).IsNull();
  }

  [Test]
  public async Task UpdateProductCommand_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var productId = IdProvider.NewGuid();
    var cmd1 = new UpdateProductCommand {
      ProductId = productId,
      Name = "Name",
      Description = null,
      Price = 29.99m,
      ImageUrl = null
    };

    var cmd2 = new UpdateProductCommand {
      ProductId = productId,
      Name = "Name",
      Description = null,
      Price = 29.99m,
      ImageUrl = null
    };

    // Act & Assert
    await Assert.That(cmd1).IsEqualTo(cmd2);
  }

  [Test]
  public async Task UpdateProductCommand_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var productId = IdProvider.NewGuid();
    var cmd = new UpdateProductCommand {
      ProductId = productId,
      Name = "ToString Test",
      Description = null,
      Price = null,
      ImageUrl = null
    };

    var stringRep = cmd.ToString();

    // Assert
    await Assert.That(stringRep).Contains(productId.ToString());
    await Assert.That(stringRep).IsNotNull();
  }
}
