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
/// Updates order read model when payment fails.
/// Listens to PaymentFailedEvent.
/// </summary>
public class PaymentFailedPerspective : IPerspectiveOf<PaymentFailedEvent> {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<PaymentFailedPerspective> _logger;

  public PaymentFailedPerspective(
    IDbConnectionFactory connectionFactory,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<PaymentFailedPerspective> logger
  ) {
    _connectionFactory = connectionFactory;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(PaymentFailedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Create new connection for this operation
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      // 1. Update order status and payment information
      await connection.ExecuteAsync(@"
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
          'PaymentFailed',
          'PaymentFailedEvent',
          @Timestamp,
          @Details::jsonb
        )",
        new {
          @event.OrderId,
          Timestamp = DateTime.UtcNow,
          Details = JsonSerializer.Serialize(
            new PaymentFailedDetails {
              Reason = @event.Reason
            },
            PerspectiveJsonContext.Default.PaymentFailedDetails
          )
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

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
