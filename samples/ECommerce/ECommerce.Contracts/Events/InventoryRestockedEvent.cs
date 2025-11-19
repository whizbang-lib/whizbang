using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when inventory is replenished
/// </summary>
public record InventoryRestockedEvent : IEvent {
  public required string ProductId { get; init; }
  public int QuantityAdded { get; init; }
  public int NewTotalQuantity { get; init; }
  public DateTime RestockedAt { get; init; }
}
