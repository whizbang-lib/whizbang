using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Lenses;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles RestockInventoryCommand and publishes InventoryRestockedEvent
/// </summary>
public class RestockInventoryReceptor(
  IDispatcher dispatcher,
  IInventoryLens inventoryLens,
  ILogger<RestockInventoryReceptor> logger) : IReceptor<RestockInventoryCommand, InventoryRestockedEvent> {
  private readonly IDispatcher _dispatcher = dispatcher;
  private readonly IInventoryLens _inventoryLens = inventoryLens;
  private readonly ILogger<RestockInventoryReceptor> _logger = logger;

  public async ValueTask<InventoryRestockedEvent> HandleAsync(
    RestockInventoryCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Restocking inventory for product {ProductId} with quantity {Quantity}",
      message.ProductId,
      message.QuantityToAdd);

    // Query current inventory level to calculate new total quantity
    var currentInventory = await _inventoryLens.GetByProductIdAsync(message.ProductId, cancellationToken);

    // Calculate new total quantity (current + added)
    // If inventory doesn't exist yet, start from 0 (defensive - should have been initialized by ProductCreatedEvent)
    var currentQuantity = currentInventory?.Quantity ?? 0;
    var newTotalQuantity = currentQuantity + message.QuantityToAdd;

    _logger.LogInformation(
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
    await _dispatcher.PublishAsync(inventoryRestocked);

    _logger.LogInformation(
      "Inventory restocked for product {ProductId}",
      message.ProductId);

    return inventoryRestocked;
  }
}
