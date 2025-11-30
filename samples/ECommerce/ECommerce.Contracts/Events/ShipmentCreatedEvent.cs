using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when a shipment is created
/// </summary>
public record ShipmentCreatedEvent : IEvent {
  [StreamKey]
  public required string OrderId { get; init; }
  public required string ShipmentId { get; init; }
  public required string TrackingNumber { get; init; }
}
