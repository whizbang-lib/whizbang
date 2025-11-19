using ECommerce.Contracts.Commands;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Commands;

/// <summary>
/// Tests for CreateProductCommand
/// </summary>
public class CreateProductCommandTests {
  [Test]
  public async Task CreateProductCommand_WithAllProperties_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new CreateProductCommand {
      ProductId = "prod-123",
      Name = "Quality Widget",
      Description = "A high-quality widget for all your needs",
      Price = 29.99m,
      ImageUrl = "https://example.com/widget.jpg",
      InitialStock = 100
    };

    // Assert
    await Assert.That(cmd.ProductId).IsEqualTo("prod-123");
    await Assert.That(cmd.Name).IsEqualTo("Quality Widget");
    await Assert.That(cmd.Description).IsEqualTo("A high-quality widget for all your needs");
    await Assert.That(cmd.Price).IsEqualTo(29.99m);
    await Assert.That(cmd.ImageUrl).IsEqualTo("https://example.com/widget.jpg");
    await Assert.That(cmd.InitialStock).IsEqualTo(100);
  }

  [Test]
  public async Task CreateProductCommand_WithNullImageUrl_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new CreateProductCommand {
      ProductId = "prod-456",
      Name = "Basic Widget",
      Description = "No image widget",
      Price = 19.99m,
      ImageUrl = null,
      InitialStock = 50
    };

    // Assert
    await Assert.That(cmd.ImageUrl).IsNull();
    await Assert.That(cmd.InitialStock).IsEqualTo(50);
  }

  [Test]
  public async Task CreateProductCommand_WithZeroInitialStock_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var cmd = new CreateProductCommand {
      ProductId = "prod-789",
      Name = "Pre-order Product",
      Description = "Coming soon",
      Price = 49.99m,
      ImageUrl = null,
      InitialStock = 0
    };

    // Assert
    await Assert.That(cmd.InitialStock).IsEqualTo(0);
  }

  [Test]
  public async Task CreateProductCommand_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var cmd1 = new CreateProductCommand {
      ProductId = "prod-123",
      Name = "Widget",
      Description = "Description",
      Price = 29.99m,
      ImageUrl = "https://example.com/img.jpg",
      InitialStock = 100
    };

    var cmd2 = new CreateProductCommand {
      ProductId = "prod-123",
      Name = "Widget",
      Description = "Description",
      Price = 29.99m,
      ImageUrl = "https://example.com/img.jpg",
      InitialStock = 100
    };

    // Act & Assert
    await Assert.That(cmd1).IsEqualTo(cmd2);
  }

  [Test]
  public async Task CreateProductCommand_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var cmd = new CreateProductCommand {
      ProductId = "prod-999",
      Name = "ToString Test",
      Description = "Test",
      Price = 99.99m,
      ImageUrl = null,
      InitialStock = 5
    };

    var stringRep = cmd.ToString();

    // Assert
    await Assert.That(stringRep).Contains("prod-999");
    await Assert.That(stringRep).IsNotNull();
  }
}
