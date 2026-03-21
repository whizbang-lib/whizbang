using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Contracts.Lenses;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Perspectives;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Perspectives.Sync;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles RestockInventoryCommand and publishes InventoryRestockedEvent.
/// Uses [AwaitPerspectiveSync] to ensure the product exists in the perspective
/// (ProductCreatedEvent has been processed) before restocking.
/// </summary>
[AwaitPerspectiveSync(typeof(InventoryLevelsPerspective), EventTypes = [typeof(ProductCreatedEvent)])]
public class RestockInventoryReceptor(
  IDispatcher dispatcher,
  IInventoryLens inventoryLens,
  ILogger<RestockInventoryReceptor> logger) : IReceptor<RestockInventoryCommand, InventoryRestockedEvent> {
  public async ValueTask<InventoryRestockedEvent> HandleAsync(
    RestockInventoryCommand message,
    CancellationToken cancellationToken = default) {

    logger.LogInformation(
      "Restocking inventory for product {ProductId} with quantity {Quantity}",
      message.ProductId,
      message.QuantityToAdd);

    // Query current inventory level to calculate new total quantity
    var currentInventory = await inventoryLens.GetByProductIdAsync(message.ProductId, cancellationToken);

    // Calculate new total quantity (current + added)
    // If inventory doesn't exist yet, start from 0 (defensive - should have been initialized by ProductCreatedEvent)
    var currentQuantity = currentInventory?.Quantity ?? 0;
    var newTotalQuantity = currentQuantity + message.QuantityToAdd;

    logger.LogInformation(
      "Restocking: Product {ProductId} current={Current}, adding={Adding}, new total={NewTotal}",
      message.ProductId,
      currentQuantity,
      message.QuantityToAdd,
      newTotalQuantity);

    // Create InventoryRestockedEvent
    var inventoryRestocked = new InventoryRestockedEvent {
      ProductId = message.ProductId,
      QuantityAdded = message.QuantityToAdd,
      NewTotalQuantity = newTotalQuantity,
      RestockedAt = DateTime.UtcNow
    };

    // Publish the event
    await dispatcher.PublishAsync(inventoryRestocked);

    logger.LogInformation(
      "Inventory restocked for product {ProductId}",
      message.ProductId);

    return inventoryRestocked;
  }
}
