using ECommerce.Contracts.Events;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Events;

/// <summary>
/// Tests for ProductDeletedEvent
/// </summary>
public class ProductDeletedEventTests {
  [Test]
  public async Task ProductDeletedEvent_WithValidProperties_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var deletedAt = DateTime.UtcNow;
    var evt = new ProductDeletedEvent {
      ProductId = "prod-123",
      DeletedAt = deletedAt
    };

    // Assert
    await Assert.That(evt.ProductId).IsEqualTo("prod-123");
    await Assert.That(evt.DeletedAt).IsEqualTo(deletedAt);
  }

  [Test]
  public async Task ProductDeletedEvent_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var deletedAt = DateTime.UtcNow;
    var evt1 = new ProductDeletedEvent {
      ProductId = "prod-123",
      DeletedAt = deletedAt
    };

    var evt2 = new ProductDeletedEvent {
      ProductId = "prod-123",
      DeletedAt = deletedAt
    };

    // Act & Assert
    await Assert.That(evt1).IsEqualTo(evt2);
  }

  [Test]
  public async Task ProductDeletedEvent_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var evt = new ProductDeletedEvent {
      ProductId = "prod-999",
      DeletedAt = DateTime.UtcNow
    };

    var stringRep = evt.ToString();

    // Assert
    await Assert.That(stringRep).Contains("prod-999");
    await Assert.That(stringRep).IsNotNull();
  }

  [Test]
  public async Task ProductDeletedEvent_WithDifferentProductIds_AreNotEqualAsync() {
    // Arrange
    var deletedAt = DateTime.UtcNow;
    var evt1 = new ProductDeletedEvent {
      ProductId = "prod-123",
      DeletedAt = deletedAt
    };

    var evt2 = new ProductDeletedEvent {
      ProductId = "prod-456",
      DeletedAt = deletedAt
    };

    // Act & Assert
    await Assert.That(evt1).IsNotEqualTo(evt2);
  }
}
