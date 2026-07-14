using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when a shipment is created
/// </summary>
[PinnedId("07967541-b4a0-4324-8bc7-fb30e264f393")]
public record ShipmentCreatedEvent : IEvent {
  [StreamId]
  public required string OrderId { get; init; }
  public required string ShipmentId { get; init; }
  public required string TrackingNumber { get; init; }
}
