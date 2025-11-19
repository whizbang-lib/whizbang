using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.PaymentWorker.Perspectives;

/// <summary>
/// Listens to InventoryReservedEvent and dispatches ProcessPaymentCommand
/// This creates a chain: InventoryReserved → ProcessPayment
/// </summary>
public class InventoryPaymentPerspective : IPerspectiveOf<InventoryReservedEvent> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<InventoryPaymentPerspective> _logger;

  public InventoryPaymentPerspective(IDispatcher dispatcher, ILogger<InventoryPaymentPerspective> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async Task Update(InventoryReservedEvent @event, CancellationToken cancellationToken = default) {
    _logger.LogInformation(
      "Inventory reserved for order {OrderId}, initiating payment processing",
      @event.OrderId);

    // Calculate payment amount based on inventory reservation
    // In a real system, this would look up order details from a database
    var paymentAmount = 100.00m; // Hardcoded for demo

    var processPaymentCommand = new ProcessPaymentCommand {
      OrderId = @event.OrderId,
      CustomerId = "CUST-123", // In real system, get from order
      Amount = paymentAmount
    };

    await _dispatcher.SendAsync(processPaymentCommand);

    _logger.LogInformation(
      "Dispatched payment processing command for order {OrderId}",
      @event.OrderId);
  }
}
