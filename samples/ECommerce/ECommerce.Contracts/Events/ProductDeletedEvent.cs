using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when a product is soft-deleted from catalog
/// </summary>
public record ProductDeletedEvent : IEvent {
  [AggregateId]
  public required Guid ProductId { get; init; }
  public DateTime DeletedAt { get; init; }
}
