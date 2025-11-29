using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles RestockInventoryCommand and publishes InventoryRestockedEvent
/// </summary>
public class RestockInventoryReceptor(IDispatcher dispatcher, ILogger<RestockInventoryReceptor> logger) : IReceptor<RestockInventoryCommand, InventoryRestockedEvent> {
  private readonly IDispatcher _dispatcher = dispatcher;
  private readonly ILogger<RestockInventoryReceptor> _logger = logger;

  public async ValueTask<InventoryRestockedEvent> HandleAsync(
    RestockInventoryCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Restocking inventory for product {ProductId} with quantity {Quantity}",
      message.ProductId,
      message.QuantityToAdd);

    // Create InventoryRestockedEvent
    // Note: For now, NewTotalQuantity equals QuantityAdded since we don't have state tracking yet
    var inventoryRestocked = new InventoryRestockedEvent {
      ProductId = message.ProductId,
      QuantityAdded = message.QuantityToAdd,
      NewTotalQuantity = message.QuantityToAdd,
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
