using System.Data;
using System.Text.Json;
using Dapper;
using ECommerce.BFF.API.Hubs;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Maintains BFF read model for orders and pushes real-time updates via SignalR.
/// Listens to OrderCreatedEvent and updates the denormalized bff.orders table.
/// </summary>
public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IDbConnection _db;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<OrderPerspective> _logger;

  public OrderPerspective(
    IDbConnection db,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<OrderPerspective> logger
  ) {
    _db = db;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // 1. Insert into bff.orders read model (denormalized)
      await _db.ExecuteAsync(@"
        INSERT INTO bff.orders (
          order_id,
          customer_id,
          tenant_id,
          status,
          total_amount,
          created_at,
          updated_at,
          line_items,
          item_count
        )
        VALUES (
          @OrderId,
          @CustomerId,
          NULL,  -- TODO: Add tenant_id when multi-tenancy implemented
          'Created',
          @TotalAmount,
          @CreatedAt,
          @CreatedAt,
          @LineItems::jsonb,
          @ItemCount
        )
        ON CONFLICT (order_id) DO UPDATE SET
          updated_at = EXCLUDED.updated_at",
        new {
          @event.OrderId,
          @event.CustomerId,
          @event.TotalAmount,
          @event.CreatedAt,
          LineItems = JsonSerializer.Serialize(@event.LineItems),
          ItemCount = @event.LineItems.Count
        });

      // 2. Insert into order status history
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
          'Created',
          'OrderCreatedEvent',
          @CreatedAt,
          @Details::jsonb
        )",
        new {
          @event.OrderId,
          @event.CreatedAt,
          Details = JsonSerializer.Serialize(new {
            totalAmount = @event.TotalAmount,
            itemCount = @event.LineItems.Count
          })
        });

      // 3. Push real-time update via SignalR
      await _hubContext.Clients.User(@event.CustomerId)
        .SendAsync("OrderStatusChanged", new OrderStatusUpdate {
          OrderId = @event.OrderId,
          Status = "Created",
          Timestamp = @event.CreatedAt,
          Message = $"Order created with total amount ${@event.TotalAmount:F2}"
        }, cancellationToken);

      _logger.LogInformation(
        "Order {OrderId} perspective updated: Status=Created, Customer={CustomerId}, Total={TotalAmount}",
        @event.OrderId,
        @event.CustomerId,
        @event.TotalAmount
      );
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Error updating OrderPerspective for order {OrderId}",
        @event.OrderId
      );
      throw;
    }
  }
}
