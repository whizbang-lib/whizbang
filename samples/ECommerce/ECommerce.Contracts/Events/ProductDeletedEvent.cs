using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when a product is soft-deleted from catalog
/// </summary>
[PinnedId("ad05b37b-a161-4215-88a9-fae9672d8d43")]
public record ProductDeletedEvent : IEvent {
  [StreamId]
  public required Guid ProductId { get; init; }
  public DateTime DeletedAt { get; init; }
}
