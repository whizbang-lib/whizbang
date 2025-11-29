using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.ShippingWorker.Perspectives;

/// <summary>
/// Perspective that observes PaymentProcessedEvent for analytics/logging
/// This demonstrates that PaymentProcessedEvent has BOTH a receptor AND a perspective
/// </summary>
public class PaymentShippingPerspective(ILogger<PaymentShippingPerspective> logger) : IPerspectiveOf<PaymentProcessedEvent> {
  private readonly ILogger<PaymentShippingPerspective> _logger = logger;

  public async Task Update(PaymentProcessedEvent @event, CancellationToken cancellationToken = default) {
    _logger.LogInformation(
      "Payment processed event observed: Order {OrderId}, Transaction {TransactionId}, Amount ${Amount}",
      @event.OrderId,
      @event.TransactionId,
      @event.Amount);

    // In a real system, this might:
    // - Update shipping analytics dashboard
    // - Calculate estimated delivery time
    // - Notify warehouse system
    // - Update order status in read model

    await Task.CompletedTask; // Async for demo
  }
}
