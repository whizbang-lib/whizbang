using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Updates order read model when inventory is reserved.
/// Uses IPerspectiveStore for zero-reflection, AOT-compatible updates with record 'with' expressions.
/// </summary>
public partial class InventoryPerspective(
  IPerspectiveStore<OrderReadModel> store,
  ILensQuery<OrderReadModel> query,
  IHubContext<OrderStatusHub> hubContext,
  ILogger<InventoryPerspective> logger
  ) : IPerspectiveOf<InventoryReservedEvent> {
  private readonly IPerspectiveStore<OrderReadModel> _store = store;
  private readonly ILensQuery<OrderReadModel> _query = query;
  private readonly IHubContext<OrderStatusHub> _hubContext = hubContext;
  private readonly ILogger<InventoryPerspective> _logger = logger;

  public async Task Update(InventoryReservedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // 1. Read existing order
      var existing = await _query.GetByIdAsync(Guid.Parse(@event.OrderId), cancellationToken);

      if (existing is null) {
        _logger.LogWarning("Order {OrderId} not found for inventory update", @event.OrderId);
        return;
      }

      // 2. Update using record 'with' expression (type-safe, zero reflection)
      var updated = existing with {
        Status = "InventoryReserved",
        UpdatedAt = DateTime.UtcNow
      };

      // 3. Write back to store
      await _store.UpsertAsync(Guid.Parse(@event.OrderId).ToString(), updated, cancellationToken);

      // 4. Push SignalR update using existing CustomerId
      await _hubContext.Clients.User(existing.CustomerId.ToString())
        .SendAsync("OrderStatusChanged", new OrderStatusUpdate {
          OrderId = @event.OrderId,
          Status = "InventoryReserved",
          Timestamp = DateTime.UtcNow,
          Message = "Inventory has been reserved for your order"
        }, cancellationToken);

      _logger.LogInformation(
        "Inventory reserved for order {OrderId}, ProductId={ProductId}, Quantity={Quantity}",
        @event.OrderId,
        @event.ProductId,
        @event.Quantity
      );
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Error updating InventoryPerspective for order {OrderId}",
        @event.OrderId
      );
      throw;
    }
  }
}
