using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Receptors;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.InventoryWorker.Tests.Receptors;

/// <summary>
/// Tests for DeleteProductReceptor
/// </summary>
public class DeleteProductReceptorTests {
  [Test]
  public async Task HandleAsync_WithValidCommand_ReturnsProductDeletedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<DeleteProductReceptor>();
    var receptor = new DeleteProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new DeleteProductCommand {

      ProductId = productId,
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.ProductId).IsEqualTo(productId);
  }

  [Test]
  [Obsolete]
  public async Task HandleAsync_PublishesProductDeletedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<DeleteProductReceptor>();
    var receptor = new DeleteProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new DeleteProductCommand {

      ProductId = productId,
    };

    // Act
    await receptor.HandleAsync(command);

    // Assert
    await Assert.That(dispatcher.PublishedEvents).HasCount().EqualTo(1);
    await Assert.That(dispatcher.PublishedEvents[0]).IsTypeOf<ProductDeletedEvent>();

    var publishedEvent = (ProductDeletedEvent)dispatcher.PublishedEvents[0];
    await Assert.That(publishedEvent.ProductId).IsEqualTo(productId);
  }

  [Test]
  public async Task HandleAsync_SetsDeletedAtTimestampAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<DeleteProductReceptor>();
    var receptor = new DeleteProductReceptor(dispatcher, logger);

    var beforeCall = DateTime.UtcNow;

    var productId = Guid.CreateVersion7();
    var command = new DeleteProductCommand {

      ProductId = productId,
    };

    // Act
    var result = await receptor.HandleAsync(command);

    var afterCall = DateTime.UtcNow;

    // Assert
    await Assert.That(result.DeletedAt).IsGreaterThanOrEqualTo(beforeCall);
    await Assert.That(result.DeletedAt).IsLessThanOrEqualTo(afterCall);
  }

  [Test]
  [Obsolete]
  public async Task HandleAsync_LogsInformation_AboutProductDeletionAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<DeleteProductReceptor>();
    var receptor = new DeleteProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new DeleteProductCommand {

      ProductId = productId,
    };

    // Act
    await receptor.HandleAsync(command);

    // Assert
    await Assert.That(logger.LoggedMessages).HasCount().GreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task HandleAsync_WithCancellationToken_CompletesSuccessfullyAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<DeleteProductReceptor>();
    var receptor = new DeleteProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new DeleteProductCommand {

      ProductId = productId,
    };

    var cts = new CancellationTokenSource();

    // Act
    var result = await receptor.HandleAsync(command, cts.Token);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  [Obsolete]
  public async Task HandleAsync_WithDifferentProductIds_CreatesCorrectEventsAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<DeleteProductReceptor>();
    var receptor = new DeleteProductReceptor(dispatcher, logger);

    var productId1 = Guid.CreateVersion7();
    var command1 = new DeleteProductCommand { ProductId = productId1 };
    var productId2 = Guid.CreateVersion7();
    var command2 = new DeleteProductCommand { ProductId = productId2 };

    // Act
    var result1 = await receptor.HandleAsync(command1);
    var result2 = await receptor.HandleAsync(command2);

    // Assert
    await Assert.That(result1.ProductId).IsEqualTo(productId1);
    await Assert.That(result2.ProductId).IsEqualTo(productId2);
    await Assert.That(dispatcher.PublishedEvents).HasCount().EqualTo(2);
  }
}
