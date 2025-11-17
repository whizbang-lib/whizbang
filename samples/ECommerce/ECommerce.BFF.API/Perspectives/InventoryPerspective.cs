using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using ECommerce.BFF.API.Hubs;
using ECommerce.Contracts.Events;
using ECommerce.Contracts.Generated;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Updates order read model when inventory is reserved.
/// Listens to InventoryReservedEvent.
/// </summary>
public class InventoryPerspective : IPerspectiveOf<InventoryReservedEvent> {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<InventoryPerspective> _logger;

  public InventoryPerspective(
    IDbConnectionFactory connectionFactory,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<InventoryPerspective> logger
  ) {
    _connectionFactory = connectionFactory;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(InventoryReservedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Create new connection for this operation
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      // 1. Update order status in bff.orders
      await connection.ExecuteAsync(@"
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
      await connection.ExecuteAsync(@"
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
          Details = JsonSerializer.Serialize(
            new InventoryReservedDetails {
              ProductId = @event.ProductId,
              Quantity = @event.Quantity,
              ReservedAt = @event.ReservedAt
            },
            PerspectiveJsonContext.Default.InventoryReservedDetails
          )
        });

      // 3. Push SignalR update
      var customerId = await connection.QuerySingleOrDefaultAsync<string>(
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

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
