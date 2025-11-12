using System.Data;
using System.Text.Json;
using Dapper;
using ECommerce.BFF.API.Hubs;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Updates order read model when inventory is reserved.
/// Listens to InventoryReservedEvent.
/// </summary>
public class InventoryPerspective : IPerspectiveOf<InventoryReservedEvent> {
  private readonly IDbConnection _db;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<InventoryPerspective> _logger;

  public InventoryPerspective(
    IDbConnection db,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<InventoryPerspective> logger
  ) {
    _db = db;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(InventoryReservedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // 1. Update order status in bff.orders
      await _db.ExecuteAsync(@"
        UPDATE bff.orders
        SET
          status = 'InventoryReserved',
          updated_at = @Timestamp
        WHERE order_id = @OrderId",
        new {
          @event.OrderId,
          Timestamp = DateTime.UtcNow
        });

      // 2. Add to status history
      await _db.ExecuteAsync(@"
        INSERT INTO bff.order_status_history (
          order_id,
          status,
          event_type,
          timestamp,
          details
        )
        VALUES (
          @OrderId,
          'InventoryReserved',
          'InventoryReservedEvent',
          @Timestamp,
          @Details::jsonb
        )",
        new {
          @event.OrderId,
          Timestamp = DateTime.UtcNow,
          Details = JsonSerializer.Serialize(new {
            productId = @event.ProductId,
            quantity = @event.Quantity,
            reservedAt = @event.ReservedAt
          })
        });

      // 3. Push SignalR update
      var customerId = await _db.QuerySingleOrDefaultAsync<string>(
        "SELECT customer_id FROM bff.orders WHERE order_id = @OrderId",
        new { @event.OrderId }
      );

      if (customerId != null) {
        await _hubContext.Clients.User(customerId)
          .SendAsync("OrderStatusChanged", new OrderStatusUpdate {
            OrderId = @event.OrderId,
            Status = "InventoryReserved",
            Timestamp = DateTime.UtcNow,
            Message = "Inventory has been reserved for your order"
          }, cancellationToken);
      }

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
