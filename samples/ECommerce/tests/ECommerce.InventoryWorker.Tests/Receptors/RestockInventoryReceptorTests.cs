using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Receptors;
using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Tests.Receptors;

/// <summary>
/// Tests for RestockInventoryReceptor
/// </summary>
public class RestockInventoryReceptorTests {
  [Test]
  public async Task HandleAsync_WithValidCommand_ReturnsInventoryRestockedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var inventoryLens = new TestInventoryLens();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, inventoryLens, logger);

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
  [Obsolete]
  public async Task HandleAsync_PublishesInventoryRestockedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var inventoryLens = new TestInventoryLens();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, inventoryLens, logger);

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
    var inventoryLens = new TestInventoryLens();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, inventoryLens, logger);

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
  [Obsolete]
  public async Task HandleAsync_LogsInformation_AboutRestockingAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var inventoryLens = new TestInventoryLens();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, inventoryLens, logger);

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
    var inventoryLens = new TestInventoryLens();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, inventoryLens, logger);

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
    var inventoryLens = new TestInventoryLens();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, inventoryLens, logger);

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
    var inventoryLens = new TestInventoryLens();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, inventoryLens, logger);

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
    var inventoryLens = new TestInventoryLens();
    var logger = new TestLogger<RestockInventoryReceptor>();
    var receptor = new RestockInventoryReceptor(dispatcher, inventoryLens, logger);

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

/// <summary>
/// Test double for IInventoryLens that returns configurable inventory levels
/// </summary>
internal class TestInventoryLens : IInventoryLens {
  private readonly Dictionary<Guid, InventoryLevelDto> _inventory = [];

  public void SetInventory(Guid productId, int quantity, int reserved = 0) {
    _inventory[productId] = new InventoryLevelDto {
      ProductId = productId,
      Quantity = quantity,
      Reserved = reserved,
      Available = quantity - reserved,
      LastUpdated = DateTime.UtcNow
    };
  }

  public Task<InventoryLevelDto?> GetByProductIdAsync(Guid productId, CancellationToken cancellationToken = default) {
    _inventory.TryGetValue(productId, out var inventory);
    return Task.FromResult(inventory);
  }

  public Task<IReadOnlyList<InventoryLevelDto>> GetAllAsync(CancellationToken cancellationToken = default) {
    IReadOnlyList<InventoryLevelDto> result = [.. _inventory.Values];
    return Task.FromResult(result);
  }

  public Task<IReadOnlyList<InventoryLevelDto>> GetLowStockAsync(int threshold = 10, CancellationToken cancellationToken = default) {
    IReadOnlyList<InventoryLevelDto> result = [.. _inventory.Values.Where(i => i.Available <= threshold)];
    return Task.FromResult(result);
  }
}
