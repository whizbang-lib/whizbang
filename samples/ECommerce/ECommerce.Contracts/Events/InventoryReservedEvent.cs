using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when inventory is successfully reserved
/// </summary>
[PinnedId("f692e4bc-219c-4a61-82f3-337469454e1b")]
public record InventoryReservedEvent : IEvent {
  public required string OrderId { get; init; }
  [StreamId]
  public required Guid ProductId { get; init; }
  public int Quantity { get; init; }
  public DateTime ReservedAt { get; init; }
}
