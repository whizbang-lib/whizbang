using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles ReserveInventoryCommand and publishes InventoryReservedEvent
/// </summary>
public class ReserveInventoryReceptor : IReceptor<ReserveInventoryCommand, InventoryReservedEvent> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<ReserveInventoryReceptor> _logger;

  public ReserveInventoryReceptor(IDispatcher dispatcher, ILogger<ReserveInventoryReceptor> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async Task<InventoryReservedEvent> HandleAsync(
    ReserveInventoryCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Reserving {Quantity} units of product {ProductId} for order {OrderId}",
      message.Quantity,
      message.ProductId,
      message.OrderId);

    // Check inventory availability (business logic would go here)
    // In a real system, this would query a database

    // Reserve the inventory
    var inventoryReserved = new InventoryReservedEvent {
      OrderId = message.OrderId,
      ProductId = message.ProductId,
      Quantity = message.Quantity,
      ReservedAt = DateTime.UtcNow
    };

    // Publish the event
    await _dispatcher.PublishAsync(inventoryReserved);

    _logger.LogInformation(
      "Inventory reserved for product {ProductId} in order {OrderId}",
      message.ProductId,
      message.OrderId);

    return inventoryReserved;
  }
}
