using Microsoft.Extensions.Logging;
using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.ShippingWorker.Receptors;

/// <summary>
/// Handles PaymentProcessedEvent DIRECTLY and dispatches CreateShipmentCommand
/// This demonstrates that EVENTS can have RECEPTORS (not just perspectives!)
/// </summary>
public class PaymentShippingReceptor : IReceptor<PaymentProcessedEvent, CreateShipmentCommand> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<PaymentShippingReceptor> _logger;

  public PaymentShippingReceptor(IDispatcher dispatcher, ILogger<PaymentShippingReceptor> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async Task<CreateShipmentCommand> HandleAsync(
    PaymentProcessedEvent message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Payment processed for order {OrderId}, initiating shipment creation",
      message.OrderId);

    // In a real system, would look up shipping address from order
    var createShipmentCommand = new CreateShipmentCommand {
      OrderId = message.OrderId,
      ShippingAddress = "123 Main St, City, State 12345"
    };

    // Dispatch the command
    await _dispatcher.SendAsync<ShipmentCreatedEvent>(createShipmentCommand);

    _logger.LogInformation(
      "Dispatched create shipment command for order {OrderId}",
      message.OrderId);

    return createShipmentCommand;
  }
}
