using Microsoft.Extensions.Logging;
using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.PaymentWorker.Receptors;

/// <summary>
/// Handles ProcessPaymentCommand and publishes PaymentProcessedEvent or PaymentFailedEvent
/// </summary>
public class ProcessPaymentReceptor : IReceptor<ProcessPaymentCommand, PaymentProcessedEvent> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<ProcessPaymentReceptor> _logger;

  public ProcessPaymentReceptor(IDispatcher dispatcher, ILogger<ProcessPaymentReceptor> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async Task<PaymentProcessedEvent> HandleAsync(
    ProcessPaymentCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Processing payment of ${Amount} for customer {CustomerId} and order {OrderId}",
      message.Amount,
      message.CustomerId,
      message.OrderId);

    // Simulate payment processing logic
    // In a real system, this would call a payment gateway API
    var random = new Random();
    var shouldSucceed = random.Next(100) < 90; // 90% success rate for demo

    if (shouldSucceed) {
      // Payment successful
      var paymentProcessed = new PaymentProcessedEvent {
        OrderId = message.OrderId,
        CustomerId = message.CustomerId,
        Amount = message.Amount,
        TransactionId = $"TXN-{Guid.NewGuid():N}"
      };

      // Publish success event
      await _dispatcher.PublishAsync(paymentProcessed);

      _logger.LogInformation(
        "Payment processed successfully for order {OrderId} with transaction {TransactionId}",
        message.OrderId,
        paymentProcessed.TransactionId);

      return paymentProcessed;
    } else {
      // Payment failed
      var paymentFailed = new PaymentFailedEvent {
        OrderId = message.OrderId,
        CustomerId = message.CustomerId,
        Reason = "Insufficient funds"
      };

      // Publish failure event
      await _dispatcher.PublishAsync(paymentFailed);

      _logger.LogWarning(
        "Payment failed for order {OrderId}: {Reason}",
        message.OrderId,
        paymentFailed.Reason);

      // We still need to return a PaymentProcessedEvent to satisfy the interface
      // In a real system, you might use a Result<T> type or throw an exception
      throw new InvalidOperationException($"Payment failed: {paymentFailed.Reason}");
    }
  }
}
