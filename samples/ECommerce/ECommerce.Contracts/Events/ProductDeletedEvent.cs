using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when a product is soft-deleted from catalog
/// </summary>
public record ProductDeletedEvent : IEvent {
  public required string ProductId { get; init; }
  public DateTime DeletedAt { get; init; }
}
