using ECommerce.Contracts.Commands;
using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when an order is successfully created
/// </summary>
public record OrderCreatedEvent : IEvent {
  [StreamKey]
  public required OrderId OrderId { get; init; }
  public required CustomerId CustomerId { get; init; }
  public required List<OrderLineItem> LineItems { get; init; }
  public decimal TotalAmount { get; init; }
  public DateTime CreatedAt { get; init; }
}
