using ECommerce.Contracts.Events;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Events;

/// <summary>
/// Tests for ProductCreatedEvent
/// </summary>
public class ProductCreatedEventTests {
  [Test]
  public async Task ProductCreatedEvent_WithAllProperties_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var createdAt = DateTime.UtcNow;
    var evt = new ProductCreatedEvent {
      ProductId = "prod-123",
      Name = "Quality Widget",
      Description = "A high-quality widget for all your needs",
      Price = 29.99m,
      ImageUrl = "https://example.com/widget.jpg",
      CreatedAt = createdAt
    };

    // Assert
    await Assert.That(evt.ProductId).IsEqualTo("prod-123");
    await Assert.That(evt.Name).IsEqualTo("Quality Widget");
    await Assert.That(evt.Description).IsEqualTo("A high-quality widget for all your needs");
    await Assert.That(evt.Price).IsEqualTo(29.99m);
    await Assert.That(evt.ImageUrl).IsEqualTo("https://example.com/widget.jpg");
    await Assert.That(evt.CreatedAt).IsEqualTo(createdAt);
  }

  [Test]
  public async Task ProductCreatedEvent_WithNullImageUrl_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var evt = new ProductCreatedEvent {
      ProductId = "prod-456",
      Name = "Basic Widget",
      Description = "No image widget",
      Price = 19.99m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };

    // Assert
    await Assert.That(evt.ImageUrl).IsNull();
    await Assert.That(evt.ProductId).IsEqualTo("prod-456");
  }

  [Test]
  public async Task ProductCreatedEvent_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var createdAt = DateTime.UtcNow;
    var evt1 = new ProductCreatedEvent {
      ProductId = "prod-123",
      Name = "Widget",
      Description = "Description",
      Price = 29.99m,
      ImageUrl = "https://example.com/img.jpg",
      CreatedAt = createdAt
    };

    var evt2 = new ProductCreatedEvent {
      ProductId = "prod-123",
      Name = "Widget",
      Description = "Description",
      Price = 29.99m,
      ImageUrl = "https://example.com/img.jpg",
      CreatedAt = createdAt
    };

    // Act & Assert
    await Assert.That(evt1).IsEqualTo(evt2);
  }

  [Test]
  public async Task ProductCreatedEvent_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var evt = new ProductCreatedEvent {
      ProductId = "prod-999",
      Name = "ToString Test",
      Description = "Test",
      Price = 99.99m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };

    var stringRep = evt.ToString();

    // Assert
    await Assert.That(stringRep).Contains("prod-999");
    await Assert.That(stringRep).IsNotNull();
  }

  [Test]
  public async Task ProductCreatedEvent_WithZeroPrice_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var evt = new ProductCreatedEvent {
      ProductId = "prod-free",
      Name = "Free Product",
      Description = "No cost",
      Price = 0m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };

    // Assert
    await Assert.That(evt.Price).IsEqualTo(0m);
  }
}
