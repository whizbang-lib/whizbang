using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when inventory is successfully reserved
/// </summary>
public record InventoryReservedEvent : IEvent {
  public required string OrderId { get; init; }
  public required string ProductId { get; init; }
  public int Quantity { get; init; }
  public DateTime ReservedAt { get; init; }
}
