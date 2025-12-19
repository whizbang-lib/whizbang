using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Updates order read model when payment is processed.
/// Uses IPerspectiveStore for zero-reflection, AOT-compatible updates with record 'with' expressions.
/// </summary>
public class PaymentPerspective(
  IPerspectiveStore<OrderReadModel> store,
  ILensQuery<OrderReadModel> query,
  IHubContext<OrderStatusHub> hubContext,
  ILogger<PaymentPerspective> logger
  ) : IPerspectiveOf<PaymentProcessedEvent> {
  private readonly IPerspectiveStore<OrderReadModel> _store = store;
  private readonly ILensQuery<OrderReadModel> _query = query;
  private readonly IHubContext<OrderStatusHub> _hubContext = hubContext;
  private readonly ILogger<PaymentPerspective> _logger = logger;

  public async Task Update(PaymentProcessedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // 1. Read existing order
      var existing = await _query.GetByIdAsync(Guid.Parse(@event.OrderId), cancellationToken);

      if (existing is null) {
        _logger.LogWarning("Order {OrderId} not found for payment update", @event.OrderId);
        return;
      }

      // 2. Update using record 'with' expression (type-safe, zero reflection)
      var updated = existing with {
        Status = "PaymentProcessed",
        PaymentStatus = "Paid",
        UpdatedAt = DateTime.UtcNow
      };

      // 3. Write back to store
      await _store.UpsertAsync(Guid.Parse(@event.OrderId), updated, cancellationToken);

      // 4. Push SignalR update using existing CustomerId
      await _hubContext.Clients.User(existing.CustomerId.ToString())
        .SendAsync("OrderStatusChanged", new OrderStatusUpdate {
          OrderId = @event.OrderId,
          Status = "PaymentProcessed",
          Timestamp = DateTime.UtcNow,
          Message = $"Payment of ${@event.Amount:F2} has been processed successfully"
        }, cancellationToken);

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
