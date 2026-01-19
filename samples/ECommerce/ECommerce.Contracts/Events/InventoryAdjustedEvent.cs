using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when inventory is manually adjusted (corrections, damages, etc.)
/// </summary>
public record InventoryAdjustedEvent : IEvent {
  [AggregateId]
  [StreamKey]
  public required Guid ProductId { get; init; }
  public int QuantityChange { get; init; }
  public int NewTotalQuantity { get; init; }
  public required string Reason { get; init; }
  public DateTime AdjustedAt { get; init; }
}
