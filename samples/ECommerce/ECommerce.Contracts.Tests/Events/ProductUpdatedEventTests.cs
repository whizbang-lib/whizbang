using ECommerce.Contracts.Events;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Events;

/// <summary>
/// Tests for ProductUpdatedEvent
/// </summary>
public class ProductUpdatedEventTests {
  private static readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();
  [Test]
  public async Task ProductUpdatedEvent_WithAllPropertiesUpdated_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var productId = _idProvider.NewGuid();
    var updatedAt = DateTime.UtcNow;
    var evt = new ProductUpdatedEvent {
      ProductId = productId,
      Name = "Updated Widget",
      Description = "Updated description",
      Price = 39.99m,
      ImageUrl = "https://example.com/new-widget.jpg",
      UpdatedAt = updatedAt
    };

    // Assert
    await Assert.That(evt.ProductId).IsEqualTo(productId);
    await Assert.That(evt.Name).IsEqualTo("Updated Widget");
    await Assert.That(evt.Description).IsEqualTo("Updated description");
    await Assert.That(evt.Price).IsEqualTo(39.99m);
    await Assert.That(evt.ImageUrl).IsEqualTo("https://example.com/new-widget.jpg");
    await Assert.That(evt.UpdatedAt).IsEqualTo(updatedAt);
  }

  [Test]
  public async Task ProductUpdatedEvent_WithOnlyNameUpdated_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var productId = _idProvider.NewGuid();
    var evt = new ProductUpdatedEvent {
      ProductId = productId,
      Name = "New Name",
      Description = null,
      Price = null,
      ImageUrl = null,
      UpdatedAt = DateTime.UtcNow
    };

    // Assert
    await Assert.That(evt.ProductId).IsEqualTo(productId);
    await Assert.That(evt.Name).IsEqualTo("New Name");
    await Assert.That(evt.Description).IsNull();
    await Assert.That(evt.Price).IsNull();
    await Assert.That(evt.ImageUrl).IsNull();
  }

  [Test]
  public async Task ProductUpdatedEvent_WithAllPropertiesNull_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var evt = new ProductUpdatedEvent {
      ProductId = _idProvider.NewGuid(),
      Name = null,
      Description = null,
      Price = null,
      ImageUrl = null,
      UpdatedAt = DateTime.UtcNow
    };

    // Assert
    await Assert.That(evt.Name).IsNull();
    await Assert.That(evt.Description).IsNull();
    await Assert.That(evt.Price).IsNull();
    await Assert.That(evt.ImageUrl).IsNull();
  }

  [Test]
  public async Task ProductUpdatedEvent_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var productId = _idProvider.NewGuid();
    var updatedAt = DateTime.UtcNow;
    var evt1 = new ProductUpdatedEvent {
      ProductId = productId,
      Name = "Name",
      Description = null,
      Price = 29.99m,
      ImageUrl = null,
      UpdatedAt = updatedAt
    };

    var evt2 = new ProductUpdatedEvent {
      ProductId = productId,
      Name = "Name",
      Description = null,
      Price = 29.99m,
      ImageUrl = null,
      UpdatedAt = updatedAt
    };

    // Act & Assert
    await Assert.That(evt1).IsEqualTo(evt2);
  }

  [Test]
  public async Task ProductUpdatedEvent_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var productId = _idProvider.NewGuid();
    var evt = new ProductUpdatedEvent {
      ProductId = productId,
      Name = "ToString Test",
      Description = null,
      Price = null,
      ImageUrl = null,
      UpdatedAt = DateTime.UtcNow
    };

    var stringRep = evt.ToString();

    // Assert
    await Assert.That(stringRep).Contains(productId.ToString());
    await Assert.That(stringRep).IsNotNull();
  }
}
