using Microsoft.Extensions.Logging;
using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.ShippingWorker.Receptors;

/// <summary>
/// Handles CreateShipmentCommand and publishes ShipmentCreatedEvent
/// </summary>
public class CreateShipmentReceptor : IReceptor<CreateShipmentCommand, ShipmentCreatedEvent> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<CreateShipmentReceptor> _logger;

  public CreateShipmentReceptor(IDispatcher dispatcher, ILogger<CreateShipmentReceptor> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async ValueTask<ShipmentCreatedEvent> HandleAsync(
    CreateShipmentCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Creating shipment for order {OrderId} to address: {Address}",
      message.OrderId,
      message.ShippingAddress);

    // Simulate shipment creation
    // In a real system, this would integrate with a shipping provider API
    var shipmentCreated = new ShipmentCreatedEvent {
      OrderId = message.OrderId,
      ShipmentId = $"SHIP-{Guid.NewGuid():N}",
      TrackingNumber = $"TRK{Random.Shared.Next(100000, 999999)}"
    };

    // Publish the event
    await _dispatcher.PublishAsync(shipmentCreated);

    _logger.LogInformation(
      "Shipment created for order {OrderId} with tracking number {TrackingNumber}",
      message.OrderId,
      shipmentCreated.TrackingNumber);

    return shipmentCreated;
  }
}
