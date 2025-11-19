using ECommerce.Contracts.Commands;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Commands;

/// <summary>
/// Tests for UpdateProductCommand
/// </summary>
public class UpdateProductCommandTests {
  [Test]
  public async Task UpdateProductCommand_WithAllPropertiesUpdated_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new UpdateProductCommand {
      ProductId = "prod-123",
      Name = "Updated Widget",
      Description = "Updated description",
      Price = 39.99m,
      ImageUrl = "https://example.com/new-widget.jpg"
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo("prod-123");
    await Assert.That(cmd.Name).IsEqualTo("Updated Widget");
    await Assert.That(cmd.Description).IsEqualTo("Updated description");
    await Assert.That(cmd.Price).IsEqualTo(39.99m);
    await Assert.That(cmd.ImageUrl).IsEqualTo("https://example.com/new-widget.jpg");
  }

  [Test]
  public async Task UpdateProductCommand_WithOnlyNameUpdated_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new UpdateProductCommand {
      ProductId = "prod-456",
      Name = "New Name",
      Description = null,
      Price = null,
      ImageUrl = null
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo("prod-456");
    await Assert.That(cmd.Name).IsEqualTo("New Name");
    await Assert.That(cmd.Description).IsNull();
    await Assert.That(cmd.Price).IsNull();
    await Assert.That(cmd.ImageUrl).IsNull();
  }

  [Test]
  public async Task UpdateProductCommand_WithAllPropertiesNull_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new UpdateProductCommand {
      ProductId = "prod-789",
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
    var cmd1 = new UpdateProductCommand {
      ProductId = "prod-123",
      Name = "Name",
      Description = null,
      Price = 29.99m,
      ImageUrl = null
    };

    var cmd2 = new UpdateProductCommand {
      ProductId = "prod-123",
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
    var cmd = new UpdateProductCommand {
      ProductId = "prod-999",
      Name = "ToString Test",
      Description = null,
      Price = null,
      ImageUrl = null
    };

    var stringRep = cmd.ToString();

    // Assert
    await Assert.That(stringRep).Contains("prod-999");
    await Assert.That(stringRep).IsNotNull();
  }
}
