using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when product details are updated
/// </summary>
public record ProductUpdatedEvent : IEvent {
  [AggregateId]
  [StreamKey]
  public required Guid ProductId { get; init; }
  public string? Name { get; init; }
  public string? Description { get; init; }
  public decimal? Price { get; init; }
  public string? ImageUrl { get; init; }
  public DateTime UpdatedAt { get; init; }
}
