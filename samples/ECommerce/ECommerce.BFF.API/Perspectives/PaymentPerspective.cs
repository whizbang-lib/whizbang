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
/// Listens to PaymentProcessedEvent and updates order_perspective table (3-column JSONB pattern).
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

      // 1. Update order perspective - set Status and PaymentStatus in model_data JSONB
      await connection.ExecuteAsync(@"
        UPDATE order_perspective
        SET
          model_data = jsonb_set(
            jsonb_set(
              jsonb_set(model_data, '{Status}', '""PaymentProcessed""'),
              '{PaymentStatus}', '""Paid""'
            ),
            '{UpdatedAt}', to_jsonb(@Timestamp::text)
          ),
          metadata = jsonb_set(metadata, '{EventType}', '""PaymentProcessedEvent""'),
          updated_at = @Timestamp,
          version = version + 1
        WHERE id = @OrderId::uuid",
        new {
          OrderId = @event.OrderId.Value.ToString(),
          Timestamp = DateTime.UtcNow
        });

      // 2. Get customer ID for SignalR from scope JSONB
      var customerId = await connection.QuerySingleOrDefaultAsync<string>(
        "SELECT scope->>'CustomerId' FROM order_perspective WHERE id = @OrderId::uuid",
        new { OrderId = @event.OrderId.Value.ToString() }
      );

      // 3. Push SignalR update
      if (customerId != null) {
        await _hubContext.Clients.User(customerId)
          .SendAsync("OrderStatusChanged", new OrderStatusUpdate {
            OrderId = @event.OrderId.Value.ToString(),
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
