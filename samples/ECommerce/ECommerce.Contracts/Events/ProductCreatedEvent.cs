using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when a new product is added to the catalog
/// </summary>
public record ProductCreatedEvent : IEvent {
  public required string ProductId { get; init; }
  public required string Name { get; init; }
  public required string Description { get; init; }
  public required decimal Price { get; init; }
  public string? ImageUrl { get; init; }
  public DateTime CreatedAt { get; init; }
}
