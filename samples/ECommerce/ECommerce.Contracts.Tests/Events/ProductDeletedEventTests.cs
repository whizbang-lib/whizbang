using ECommerce.Contracts.Events;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.Contracts.Tests.Events;

/// <summary>
/// Tests for ProductDeletedEvent
/// </summary>
public class ProductDeletedEventTests {
  private static readonly IWhizbangIdProvider IdProvider = new Uuid7IdProvider();
  [Test]
  public async Task ProductDeletedEvent_WithValidProperties_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var productId = IdProvider.NewGuid();
    var deletedAt = DateTime.UtcNow;
    var evt = new ProductDeletedEvent {
      ProductId = productId,
      DeletedAt = deletedAt
    };

    // Assert
    await Assert.That(evt.ProductId).IsEqualTo(productId);
    await Assert.That(evt.DeletedAt).IsEqualTo(deletedAt);
  }

  [Test]
  public async Task ProductDeletedEvent_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var productId = IdProvider.NewGuid();
    var deletedAt = DateTime.UtcNow;
    var evt1 = new ProductDeletedEvent {
      ProductId = productId,
      DeletedAt = deletedAt
    };

    var evt2 = new ProductDeletedEvent {
      ProductId = productId,
      DeletedAt = deletedAt
    };

    // Act & Assert
    await Assert.That(evt1).IsEqualTo(evt2);
  }

  [Test]
  public async Task ProductDeletedEvent_ToString_ReturnsReadableRepresentationAsync() {
    // Arrange & Act
    var productId = IdProvider.NewGuid();
    var evt = new ProductDeletedEvent {
      ProductId = productId,
      DeletedAt = DateTime.UtcNow
    };

    var stringRep = evt.ToString();

    // Assert
    await Assert.That(stringRep).Contains(productId.ToString());
    await Assert.That(stringRep).IsNotNull();
  }

  [Test]
  public async Task ProductDeletedEvent_WithDifferentProductIds_AreNotEqualAsync() {
    // Arrange
    var deletedAt = DateTime.UtcNow;
    var evt1 = new ProductDeletedEvent {
      ProductId = IdProvider.NewGuid(),
      DeletedAt = deletedAt
    };

    var evt2 = new ProductDeletedEvent {
      ProductId = IdProvider.NewGuid(),
      DeletedAt = deletedAt
    };

    // Act & Assert
    await Assert.That(evt1).IsNotEqualTo(evt2);
  }
}
