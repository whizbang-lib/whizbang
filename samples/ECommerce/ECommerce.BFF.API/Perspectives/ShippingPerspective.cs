using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Updates order read model when shipment is created.
/// Uses IPerspectiveStore for zero-reflection, AOT-compatible updates with record 'with' expressions.
/// </summary>
public class ShippingPerspective(
  IPerspectiveStore<OrderReadModel> store,
  ILensQuery<OrderReadModel> query,
  IHubContext<OrderStatusHub> hubContext,
  ILogger<ShippingPerspective> logger
  ) : IPerspectiveOf<ShipmentCreatedEvent> {
  private readonly IPerspectiveStore<OrderReadModel> _store = store;
  private readonly ILensQuery<OrderReadModel> _query = query;
  private readonly IHubContext<OrderStatusHub> _hubContext = hubContext;
  private readonly ILogger<ShippingPerspective> _logger = logger;

  public async Task Update(ShipmentCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // 1. Read existing order
      var existing = await _query.GetByIdAsync(Guid.Parse(@event.OrderId), cancellationToken);

      if (existing is null) {
        _logger.LogWarning("Order {OrderId} not found for shipment update", @event.OrderId);
        return;
      }

      // 2. Update using record 'with' expression (type-safe, zero reflection)
      var updated = existing with {
        Status = "ShipmentCreated",
        ShipmentId = @event.ShipmentId,
        TrackingNumber = @event.TrackingNumber,
        UpdatedAt = DateTime.UtcNow
      };

      // 3. Write back to store
      await _store.UpsertAsync(Guid.Parse(@event.OrderId).ToString(), updated, cancellationToken);

      // 4. Push SignalR update using existing CustomerId
      await _hubContext.Clients.User(existing.CustomerId.ToString())
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
