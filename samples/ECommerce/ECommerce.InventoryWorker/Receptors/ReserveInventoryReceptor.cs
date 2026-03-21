using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles ReserveInventoryCommand and publishes InventoryReservedEvent
/// </summary>
public class ReserveInventoryReceptor(IDispatcher dispatcher, ILogger<ReserveInventoryReceptor> logger) : IReceptor<ReserveInventoryCommand, InventoryReservedEvent> {
  public async ValueTask<InventoryReservedEvent> HandleAsync(
    ReserveInventoryCommand message,
    CancellationToken cancellationToken = default) {

    logger.LogInformation(
      "Reserving {Quantity} units of product {ProductId} for order {OrderId}",
      message.Quantity,
      message.ProductId,
      message.OrderId);

    // Check inventory availability (business logic would go here)
    // In a real system, this would query a database

    // Reserve the inventory
    var inventoryReserved = new InventoryReservedEvent {
      OrderId = message.OrderId.Value.ToString(),
      ProductId = message.ProductId.Value,
      Quantity = message.Quantity,
      ReservedAt = DateTime.UtcNow
    };

    // Publish the event
    await dispatcher.PublishAsync(inventoryReserved);

    logger.LogInformation(
      "Inventory reserved for product {ProductId} in order {OrderId}",
      message.ProductId,
      message.OrderId);

    return inventoryReserved;
  }
}
