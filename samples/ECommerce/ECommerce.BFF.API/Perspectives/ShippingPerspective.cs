using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using ECommerce.BFF.API.Hubs;
using ECommerce.Contracts.Events;
using ECommerce.Contracts.Generated;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Updates order read model when shipment is created.
/// Listens to ShipmentCreatedEvent.
/// </summary>
public class ShippingPerspective : IPerspectiveOf<ShipmentCreatedEvent> {
  private readonly IDbConnection _db;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<ShippingPerspective> _logger;

  public ShippingPerspective(
    IDbConnection db,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<ShippingPerspective> logger
  ) {
    _db = db;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(ShipmentCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // 1. Update order status and shipment information
      await _db.ExecuteAsync(@"
        UPDATE bff.orders
        SET
          status = 'ShipmentCreated',
          shipment_id = @ShipmentId,
          tracking_number = @TrackingNumber,
          updated_at = @Timestamp
        WHERE order_id = @OrderId",
        new {
          @event.OrderId,
          @event.ShipmentId,
          @event.TrackingNumber,
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
          'ShipmentCreated',
          'ShipmentCreatedEvent',
          @Timestamp,
          @Details::jsonb
        )",
        new {
          @event.OrderId,
          Timestamp = DateTime.UtcNow,
          Details = JsonSerializer.Serialize(
            new OrderShippedDetails {
              ShipmentId = @event.ShipmentId,
              TrackingNumber = @event.TrackingNumber
            },
            (JsonTypeInfo<OrderShippedDetails>)WhizbangJsonContext.CreateOptions().GetTypeInfo(typeof(OrderShippedDetails))!
          )
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
            Status = "ShipmentCreated",
            Timestamp = DateTime.UtcNow,
            Message = $"Your order has been shipped. Tracking number: {@event.TrackingNumber}",
            Details = new Dictionary<string, object> {
              ["ShipmentId"] = @event.ShipmentId,
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
}
