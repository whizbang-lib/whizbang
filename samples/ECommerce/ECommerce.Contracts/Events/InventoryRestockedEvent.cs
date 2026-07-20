using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when inventory is replenished
/// </summary>
[PinnedId("2a726a5f-1f14-473b-8653-6163dcc6e5e1")]
public record InventoryRestockedEvent : IEvent {
  [StreamId]
  public required Guid ProductId { get; init; }
  public int QuantityAdded { get; init; }
  public int NewTotalQuantity { get; init; }
  public DateTime RestockedAt { get; init; }
}
