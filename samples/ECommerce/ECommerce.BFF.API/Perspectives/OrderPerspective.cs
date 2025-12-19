using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Maintains BFF read model for orders and pushes real-time updates via SignalR.
/// Listens to OrderCreatedEvent and updates the order_perspective table using IPerspectiveStore.
/// Uses EF Core with 3-column JSONB pattern - zero reflection, AOT compatible.
/// </summary>
public class OrderPerspective(
  IPerspectiveStore<OrderReadModel> store,
  ILensQuery<OrderReadModel> lens,
  IHubContext<OrderStatusHub> hubContext,
  ILogger<OrderPerspective> logger
  ) : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IPerspectiveStore<OrderReadModel> _store = store;
  private readonly ILensQuery<OrderReadModel> _lens = lens;
  private readonly IHubContext<OrderStatusHub> _hubContext = hubContext;
  private readonly ILogger<OrderPerspective> _logger = logger;

  public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Check if order already exists to preserve original CreatedAt
      var existingOrder = await _lens.GetByIdAsync(@event.OrderId.Value, cancellationToken);
      var createdAt = existingOrder?.CreatedAt ?? @event.CreatedAt;

      // Build OrderReadModel for persistence
      var orderReadModel = new OrderReadModel {
        OrderId = @event.OrderId,
        CustomerId = @event.CustomerId,
        TenantId = null,  // TODO: Add tenant_id when multi-tenancy implemented
        Status = "Created",
        TotalAmount = @event.TotalAmount,
        CreatedAt = createdAt,  // Preserve original CreatedAt on update
        UpdatedAt = @event.CreatedAt,  // Always update to event timestamp
        ItemCount = @event.LineItems.Count,
        PaymentStatus = null,
        ShipmentId = null,
        TrackingNumber = null,
        LineItems = @event.LineItems.Select(li => new LineItemReadModel {
          ProductId = li.ProductId.Value.ToString(),
          ProductName = li.ProductName,
          Quantity = li.Quantity,
          Price = li.UnitPrice
        }).ToList()
      };

      // Store handles JSON serialization, metadata, scope, timestamps, versioning
      await _store.UpsertAsync(@event.OrderId.Value.ToString(), orderReadModel, cancellationToken);

      // Push real-time update via SignalR
      await _hubContext.Clients.User(@event.CustomerId.ToString())
        .SendAsync("OrderStatusChanged", new OrderStatusUpdate {
          OrderId = @event.OrderId.ToString(),
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
