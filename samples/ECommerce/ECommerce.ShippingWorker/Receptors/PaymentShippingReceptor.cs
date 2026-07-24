using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.ShippingWorker.Receptors;

/// <summary>
/// Handles PaymentProcessedEvent DIRECTLY and dispatches CreateShipmentCommand
/// This demonstrates that EVENTS can have RECEPTORS (not just perspectives!)
/// </summary>
public class PaymentShippingReceptor(IDispatcher dispatcher, ILogger<PaymentShippingReceptor> logger) : IReceptor<PaymentProcessedEvent, CreateShipmentCommand> {

  public async ValueTask<CreateShipmentCommand> HandleAsync(
    PaymentProcessedEvent message,
    CancellationToken cancellationToken = default) {

    logger.LogInformation(
      "Payment processed for order {OrderId}, initiating shipment creation",
      message.OrderId);

    // In a real system, would look up shipping address from order
    var createShipmentCommand = new CreateShipmentCommand {
      OrderId = message.OrderId,
      ShippingAddress = "123 Main St, City, State 12345"
    };

    // Dispatch the command
    await dispatcher.SendAsync(createShipmentCommand);

    logger.LogInformation(
      "Dispatched create shipment command for order {OrderId}",
      message.OrderId);

    return createShipmentCommand;
  }
}
