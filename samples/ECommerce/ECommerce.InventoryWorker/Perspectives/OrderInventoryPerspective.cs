using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.InventoryWorker.Perspectives;

/// <summary>
/// Listens to OrderCreatedEvent and dispatches ReserveInventoryCommand for each line item
/// </summary>
public class OrderInventoryPerspective : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<OrderInventoryPerspective> _logger;

  public OrderInventoryPerspective(IDispatcher dispatcher, ILogger<OrderInventoryPerspective> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    _logger.LogInformation(
      "Processing inventory reservations for order {OrderId} with {ItemCount} items",
      @event.OrderId,
      @event.LineItems.Count);

    // Reserve inventory for each line item
    foreach (var lineItem in @event.LineItems) {
      var reserveCommand = new ReserveInventoryCommand {
        OrderId = @event.OrderId,
        ProductId = lineItem.ProductId,
        Quantity = lineItem.Quantity
      };

      await _dispatcher.SendAsync<InventoryReservedEvent>(reserveCommand);

      _logger.LogInformation(
        "Dispatched inventory reservation for product {ProductId} in order {OrderId}",
        lineItem.ProductId,
        @event.OrderId);
    }
  }
}
