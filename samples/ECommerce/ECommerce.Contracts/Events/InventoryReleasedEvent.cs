using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when previously reserved inventory is released (e.g., order cancelled)
/// </summary>
[PinnedId("098a6dcd-438d-47ba-808a-d63f057555bf")]
public record InventoryReleasedEvent : IEvent {
  public required string OrderId { get; init; }
  [StreamId]
  public required Guid ProductId { get; init; }
  public int Quantity { get; init; }
  public DateTime ReleasedAt { get; init; }
}
