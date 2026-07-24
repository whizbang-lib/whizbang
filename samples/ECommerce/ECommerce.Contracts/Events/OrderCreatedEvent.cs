using ECommerce.Contracts.Commands;
using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when an order is successfully created
/// </summary>
[PinnedId("97cff6e8-91a9-4bf4-8bc8-0619579a4158")]
public record OrderCreatedEvent : IEvent {
  [StreamId]
  public required OrderId OrderId { get; init; }
  public required CustomerId CustomerId { get; init; }
  public required List<OrderLineItem> LineItems { get; init; }
  public decimal TotalAmount { get; init; }
  public DateTime CreatedAt { get; init; }
}
