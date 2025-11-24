using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Receptors;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.InventoryWorker.Tests.Receptors;

/// <summary>
/// Tests for RestockInventoryReceptor
/// </summary>
public class RestockInventoryReceptorTests {
  [Test]
  public async Task HandleAsync_WithValidCommand_ReturnsInventoryRestockedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new RestockInventoryCommand {

      ProductId = productId,
      QuantityToAdd = 50
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.ProductId).IsEqualTo(productId);
    await Assert.That(result.QuantityAdded).IsEqualTo(50);
  }

  [Test]
  public async Task HandleAsync_PublishesInventoryRestockedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new RestockInventoryCommand {

      ProductId = productId,
      QuantityToAdd = 100
    };

    // Act
    await receptor.HandleAsync(command);

    // Assert
    await Assert.That(dispatcher.PublishedEvents).HasCount().EqualTo(1);
    await Assert.That(dispatcher.PublishedEvents[0]).IsTypeOf<InventoryRestockedEvent>();

    var publishedEvent = (InventoryRestockedEvent)dispatcher.PublishedEvents[0];
    await Assert.That(publishedEvent.ProductId).IsEqualTo(productId);
    await Assert.That(publishedEvent.QuantityAdded).IsEqualTo(100);
  }

  [Test]
  public async Task HandleAsync_SetsRestockedAtTimestampAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, logger);

    var beforeCall = DateTime.UtcNow;

    var productId = Guid.CreateVersion7();
    var command = new RestockInventoryCommand {

      ProductId = productId,
      QuantityToAdd = 25
    };

    // Act
    var result = await receptor.HandleAsync(command);

    var afterCall = DateTime.UtcNow;

    // Assert
    await Assert.That(result.RestockedAt).IsGreaterThanOrEqualTo(beforeCall);
    await Assert.That(result.RestockedAt).IsLessThanOrEqualTo(afterCall);
  }

  [Test]
  public async Task HandleAsync_LogsInformation_AboutRestockingAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new RestockInventoryCommand {

      ProductId = productId,
      QuantityToAdd = 10
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
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new RestockInventoryCommand {

      ProductId = productId,
      QuantityToAdd = 5
    };

    var cts = new CancellationTokenSource();

    // Act
    var result = await receptor.HandleAsync(command, cts.Token);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task HandleAsync_WithLargeQuantity_MapsCorrectlyAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new RestockInventoryCommand {

      ProductId = productId,
      QuantityToAdd = 10000
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result.QuantityAdded).IsEqualTo(10000);
  }

  [Test]
  public async Task HandleAsync_WithZeroQuantity_MapsCorrectlyAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new RestockInventoryCommand {

      ProductId = productId,
      QuantityToAdd = 0
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result.QuantityAdded).IsEqualTo(0);
  }

  [Test]
  public async Task HandleAsync_MapsNewTotalQuantityCorrectlyAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, logger);

    var productId = Guid.CreateVersion7();
    var command = new RestockInventoryCommand {

      ProductId = productId,
      QuantityToAdd = 50
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert - For now, NewTotalQuantity should equal QuantityAdded (no existing inventory)
    await Assert.That(result.NewTotalQuantity).IsEqualTo(50);
  }
}
