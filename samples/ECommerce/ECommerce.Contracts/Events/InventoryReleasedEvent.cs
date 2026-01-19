using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when previously reserved inventory is released (e.g., order cancelled)
/// </summary>
public record InventoryReleasedEvent : IEvent {
  public required string OrderId { get; init; }
  [AggregateId]
  [StreamKey]
  public required Guid ProductId { get; init; }
  public int Quantity { get; init; }
  public DateTime ReleasedAt { get; init; }
}
