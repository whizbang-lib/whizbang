using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Receptors;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.InventoryWorker.Tests.Receptors;

/// <summary>
/// Tests for UpdateProductReceptor
/// </summary>
public class UpdateProductReceptorTests {
  [Test]
  public async Task HandleAsync_WithAllPropertiesUpdated_ReturnsProductUpdatedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<UpdateProductReceptor>();
    var receptor = new UpdateProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new UpdateProductCommand {

      ProductId = productId,
      Name = "Updated Widget",
      Description = "Updated description",
      Price = 39.99m,
      ImageUrl = "https://example.com/new-widget.jpg"
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.ProductId).IsEqualTo(productId);
    await Assert.That(result.Name).IsEqualTo("Updated Widget");
    await Assert.That(result.Description).IsEqualTo("Updated description");
    await Assert.That(result.Price).IsEqualTo(39.99m);
    await Assert.That(result.ImageUrl).IsEqualTo("https://example.com/new-widget.jpg");
  }

  [Test]
  public async Task HandleAsync_WithOnlyNameUpdated_ReturnsEventWithOtherPropertiesNullAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<UpdateProductReceptor>();
    var receptor = new UpdateProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new UpdateProductCommand {

      ProductId = productId,
      Name = "New Name",
      Description = null,
      Price = null,
      ImageUrl = null
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result.ProductId).IsEqualTo(productId);
    await Assert.That(result.Name).IsEqualTo("New Name");
    await Assert.That(result.Description).IsNull();
    await Assert.That(result.Price).IsNull();
    await Assert.That(result.ImageUrl).IsNull();
  }

  [Test]
  public async Task HandleAsync_WithAllPropertiesNull_ReturnsEventWithNullsAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<UpdateProductReceptor>();
    var receptor = new UpdateProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new UpdateProductCommand {

      ProductId = productId,
      Name = null,
      Description = null,
      Price = null,
      ImageUrl = null
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result.ProductId).IsEqualTo(productId);
    await Assert.That(result.Name).IsNull();
    await Assert.That(result.Description).IsNull();
    await Assert.That(result.Price).IsNull();
    await Assert.That(result.ImageUrl).IsNull();
  }

  [Test]
  public async Task HandleAsync_PublishesProductUpdatedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<UpdateProductReceptor>();
    var receptor = new UpdateProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new UpdateProductCommand {

      ProductId = productId,
      Name = "Published",
      Description = null,
      Price = 29.99m,
      ImageUrl = null
    };

    // Act
    await receptor.HandleAsync(command);

    // Assert
    await Assert.That(dispatcher.PublishedEvents).HasCount().EqualTo(1);
    await Assert.That(dispatcher.PublishedEvents[0]).IsTypeOf<ProductUpdatedEvent>();

    var publishedEvent = (ProductUpdatedEvent)dispatcher.PublishedEvents[0];
    await Assert.That(publishedEvent.ProductId).IsEqualTo(productId);
    await Assert.That(publishedEvent.Name).IsEqualTo("Published");
  }

  [Test]
  public async Task HandleAsync_SetsUpdatedAtTimestampAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<UpdateProductReceptor>();
    var receptor = new UpdateProductReceptor(dispatcher, logger);

    var beforeCall = DateTime.UtcNow;

    var productId = Guid.CreateVersion7();
    var command = new UpdateProductCommand {

      ProductId = productId,
      Name = "Time Test",
      Description = null,
      Price = null,
      ImageUrl = null
    };

    // Act
    var result = await receptor.HandleAsync(command);

    var afterCall = DateTime.UtcNow;

    // Assert
    await Assert.That(result.UpdatedAt).IsGreaterThanOrEqualTo(beforeCall);
    await Assert.That(result.UpdatedAt).IsLessThanOrEqualTo(afterCall);
  }

  [Test]
  public async Task HandleAsync_LogsInformation_AboutProductUpdateAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<UpdateProductReceptor>();
    var receptor = new UpdateProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new UpdateProductCommand {

      ProductId = productId,
      Name = null,
      Description = "Updated description",
      Price = null,
      ImageUrl = null
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
    var logger = new TestLogger<UpdateProductReceptor>();
    var receptor = new UpdateProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new UpdateProductCommand {

      ProductId = productId,
      Name = "Cancel Test",
      Description = null,
      Price = null,
      ImageUrl = null
    };

    var cts = new CancellationTokenSource();

    // Act
    var result = await receptor.HandleAsync(command, cts.Token);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task HandleAsync_WithPriceUpdate_MapsCorrectlyAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<UpdateProductReceptor>();
    var receptor = new UpdateProductReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new UpdateProductCommand {

      ProductId = productId,
      Name = null,
      Description = null,
      Price = 99.99m,
      ImageUrl = null
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result.Price).IsEqualTo(99.99m);
    await Assert.That(result.Name).IsNull();
  }
}
