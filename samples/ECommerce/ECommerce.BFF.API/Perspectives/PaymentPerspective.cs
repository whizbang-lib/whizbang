using System.Data;
using System.Text.Json;
using Dapper;
using ECommerce.BFF.API.Hubs;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Updates order read model when payment is processed.
/// Listens to PaymentProcessedEvent.
/// </summary>
public class PaymentPerspective : IPerspectiveOf<PaymentProcessedEvent> {
  private readonly IDbConnection _db;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<PaymentPerspective> _logger;

  public PaymentPerspective(
    IDbConnection db,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<PaymentPerspective> logger
  ) {
    _db = db;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(PaymentProcessedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // 1. Update order status and payment information
      await _db.ExecuteAsync(@"
        UPDATE bff.orders
        SET
          status = 'PaymentProcessed',
          payment_status = 'Paid',
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
          'PaymentProcessed',
          'PaymentProcessedEvent',
          @Timestamp,
          @Details::jsonb
        )",
        new {
          @event.OrderId,
          Timestamp = DateTime.UtcNow,
          Details = JsonSerializer.Serialize(new {
            transactionId = @event.TransactionId,
            amount = @event.Amount
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
            Status = "PaymentProcessed",
            Timestamp = DateTime.UtcNow,
            Message = $"Payment of ${@event.Amount:F2} has been processed successfully"
          }, cancellationToken);
      }

      _logger.LogInformation(
        "Payment processed for order {OrderId}, TransactionId={TransactionId}, Amount={Amount}",
        @event.OrderId,
        @event.TransactionId,
        @event.Amount
      );
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Error updating PaymentPerspective for order {OrderId}",
        @event.OrderId
      );
      throw;
    }
  }
}
