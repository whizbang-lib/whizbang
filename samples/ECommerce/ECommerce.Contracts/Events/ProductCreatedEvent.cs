using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when a new product is added to the catalog
/// </summary>
[AuditEvent(Reason = "Product catalog change", Level = Whizbang.Core.Audit.AuditLevel.Info)]
public record ProductCreatedEvent : IEvent {
  [StreamId]
  public required Guid ProductId { get; init; }
  public required string Name { get; init; }
  public required string Description { get; init; }
  public required decimal Price { get; init; }
  public string? ImageUrl { get; init; }
  public DateTime CreatedAt { get; init; }
}
