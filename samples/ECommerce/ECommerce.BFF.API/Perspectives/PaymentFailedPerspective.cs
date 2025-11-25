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
/// Listens to PaymentFailedEvent and updates order_perspective table (3-column JSONB pattern).
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

      // 1. Update order perspective - set Status and PaymentStatus in model_data JSONB
      await connection.ExecuteAsync(@"
        UPDATE order_perspective
        SET
          model_data = jsonb_set(
            jsonb_set(
              jsonb_set(model_data, '{Status}', '""PaymentFailed""'),
              '{PaymentStatus}', '""Failed""'
            ),
            '{UpdatedAt}', to_jsonb(@Timestamp::text)
          ),
          metadata = jsonb_set(metadata, '{EventType}', '""PaymentFailedEvent""'),
          updated_at = @Timestamp,
          version = version + 1
        WHERE id = @OrderId::uuid",
        new {
          OrderId = @event.OrderId.Value.ToString(),
          Timestamp = DateTime.UtcNow
        });

      // 2. Push SignalR update (PaymentFailedEvent has CustomerId directly)
      await _hubContext.Clients.User(@event.CustomerId.Value.ToString())
        .SendAsync("OrderStatusChanged", new OrderStatusUpdate {
          OrderId = @event.OrderId.Value.ToString(),
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
