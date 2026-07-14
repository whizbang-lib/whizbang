using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when inventory is manually adjusted (corrections, damages, etc.)
/// </summary>
[PinnedId("28332272-6f3b-4a55-a899-fee18a3f7606")]
public record InventoryAdjustedEvent : IEvent {
  [StreamId]
  public required Guid ProductId { get; init; }
  public int QuantityChange { get; init; }
  public int NewTotalQuantity { get; init; }
  public required string Reason { get; init; }
  public DateTime AdjustedAt { get; init; }
}
