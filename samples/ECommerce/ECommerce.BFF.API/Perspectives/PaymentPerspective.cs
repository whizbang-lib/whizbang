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
/// Updates order read model when payment is processed.
/// Listens to PaymentProcessedEvent.
/// </summary>
public class PaymentPerspective : IPerspectiveOf<PaymentProcessedEvent> {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<PaymentPerspective> _logger;

  public PaymentPerspective(
    IDbConnectionFactory connectionFactory,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<PaymentPerspective> logger
  ) {
    _connectionFactory = connectionFactory;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(PaymentProcessedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Create new connection for this operation
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      // 1. Update order status and payment information
      await connection.ExecuteAsync(@"
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
          'PaymentProcessed',
          'PaymentProcessedEvent',
          @Timestamp,
          @Details::jsonb
        )",
        new {
          @event.OrderId,
          Timestamp = DateTime.UtcNow,
          Details = JsonSerializer.Serialize(
            new PaymentProcessedDetails {
              TransactionId = @event.TransactionId,
              Amount = @event.Amount
            },
            PerspectiveJsonContext.Default.PaymentProcessedDetails
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

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
