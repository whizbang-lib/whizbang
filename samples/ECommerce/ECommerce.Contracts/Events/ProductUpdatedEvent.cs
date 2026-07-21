using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when product details are updated
/// </summary>
[PinnedId("d9eaf096-ef80-4bb4-a394-d10d2fca5bed")]
public record ProductUpdatedEvent : IEvent {
  [StreamId]
  public required Guid ProductId { get; init; }
  public string? Name { get; init; }
  public string? Description { get; init; }
  public decimal? Price { get; init; }
  public string? ImageUrl { get; init; }
  public DateTime UpdatedAt { get; init; }
}
