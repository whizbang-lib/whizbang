using System.Data;
using System.Text.Json;
using Dapper;
using ECommerce.BFF.API.Hubs;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Updates order read model when payment fails.
/// Listens to PaymentFailedEvent.
/// </summary>
public class PaymentFailedPerspective : IPerspectiveOf<PaymentFailedEvent> {
  private readonly IDbConnection _db;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<PaymentFailedPerspective> _logger;

  public PaymentFailedPerspective(
    IDbConnection db,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<PaymentFailedPerspective> logger
  ) {
    _db = db;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(PaymentFailedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // 1. Update order status and payment information
      await _db.ExecuteAsync(@"
        UPDATE bff.orders
        SET
          status = 'PaymentFailed',
          payment_status = 'Failed',
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
          'PaymentFailed',
          'PaymentFailedEvent',
          @Timestamp,
          @Details::jsonb
        )",
        new {
          @event.OrderId,
          Timestamp = DateTime.UtcNow,
          Details = JsonSerializer.Serialize(new {
            reason = @event.Reason
          })
        });

      // 3. Push SignalR update
      await _hubContext.Clients.User(@event.CustomerId)
        .SendAsync("OrderStatusChanged", new OrderStatusUpdate {
          OrderId = @event.OrderId,
          Status = "PaymentFailed",
          Timestamp = DateTime.UtcNow,
          Message = $"Payment failed: {@event.Reason}",
          Details = new Dictionary<string, object> {
            ["Reason"] = @event.Reason
          }
        }, cancellationToken);

      _logger.LogWarning(
        "Payment failed for order {OrderId}, Customer={CustomerId}, Reason={Reason}",
        @event.OrderId,
        @event.CustomerId,
        @event.Reason
      );
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Error updating PaymentFailedPerspective for order {OrderId}",
        @event.OrderId
      );
      throw;
    }
  }
}
