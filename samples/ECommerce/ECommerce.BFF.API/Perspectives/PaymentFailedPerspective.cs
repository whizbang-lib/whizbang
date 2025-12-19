using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Updates order read model when payment fails.
/// Uses IPerspectiveStore for zero-reflection, AOT-compatible updates with record 'with' expressions.
/// </summary>
public class PaymentFailedPerspective(
  IPerspectiveStore<OrderReadModel> store,
  ILensQuery<OrderReadModel> query,
  IHubContext<OrderStatusHub> hubContext,
  ILogger<PaymentFailedPerspective> logger
  ) : IPerspectiveOf<PaymentFailedEvent> {
  private readonly IPerspectiveStore<OrderReadModel> _store = store;
  private readonly ILensQuery<OrderReadModel> _query = query;
  private readonly IHubContext<OrderStatusHub> _hubContext = hubContext;
  private readonly ILogger<PaymentFailedPerspective> _logger = logger;

  public async Task Update(PaymentFailedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // 1. Read existing order (OrderId is a string, parse to Guid)
      var existing = await _query.GetByIdAsync(Guid.Parse(@event.OrderId), cancellationToken);

      if (existing is null) {
        _logger.LogWarning("Order {OrderId} not found for payment failed update", @event.OrderId);
        return;
      }

      // 2. Update using record 'with' expression (type-safe, zero reflection)
      var updated = existing with {
        Status = "PaymentFailed",
        PaymentStatus = "Failed",
        UpdatedAt = DateTime.UtcNow
      };

      // 3. Write back to store
      await _store.UpsertAsync(Guid.Parse(@event.OrderId).ToString(), updated, cancellationToken);

      // 4. Push SignalR update (PaymentFailedEvent has CustomerId directly)
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
