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
/// Updates order read model when shipment is created.
/// Listens to ShipmentCreatedEvent and updates order_perspective table (3-column JSONB pattern).
/// </summary>
public class ShippingPerspective : IPerspectiveOf<ShipmentCreatedEvent> {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<ShippingPerspective> _logger;

  public ShippingPerspective(
    IDbConnectionFactory connectionFactory,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<ShippingPerspective> logger
  ) {
    _connectionFactory = connectionFactory;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(ShipmentCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Create new connection for this operation
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      // 1. Update order perspective - set Status, ShipmentId, and TrackingNumber in model_data JSONB
      await connection.ExecuteAsync(@"
        UPDATE order_perspective
        SET
          model_data = jsonb_set(
            jsonb_set(
              jsonb_set(
                jsonb_set(model_data, '{Status}', '""ShipmentCreated""'),
                '{ShipmentId}', to_jsonb(@ShipmentId::text)
              ),
              '{TrackingNumber}', to_jsonb(@TrackingNumber::text)
            ),
            '{UpdatedAt}', to_jsonb(@Timestamp::text)
          ),
          metadata = jsonb_set(metadata, '{EventType}', '""ShipmentCreatedEvent""'),
          updated_at = @Timestamp,
          version = version + 1
        WHERE id = @OrderId::uuid",
        new {
          OrderId = @event.OrderId.Value.ToString(),
          ShipmentId = @event.ShipmentId.Value.ToString(),
          TrackingNumber = @event.TrackingNumber,
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
            Status = "ShipmentCreated",
            Timestamp = DateTime.UtcNow,
            Message = $"Your order has been shipped. Tracking number: {@event.TrackingNumber}",
            Details = new Dictionary<string, object> {
              ["ShipmentId"] = @event.ShipmentId.Value.ToString(),
              ["TrackingNumber"] = @event.TrackingNumber
            }
          }, cancellationToken);
      }

      _logger.LogInformation(
        "Shipment created for order {OrderId}, ShipmentId={ShipmentId}, TrackingNumber={TrackingNumber}",
        @event.OrderId,
        @event.ShipmentId,
        @event.TrackingNumber
      );
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Error updating ShippingPerspective for order {OrderId}",
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
