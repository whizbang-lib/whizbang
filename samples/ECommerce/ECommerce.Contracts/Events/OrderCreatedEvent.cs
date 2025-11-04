using Whizbang.Core;
using ECommerce.Contracts.Commands;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when an order is successfully created
/// </summary>
public record OrderCreatedEvent : IEvent {
  public required string OrderId { get; init; }
  public required string CustomerId { get; init; }
  public required List<OrderLineItem> LineItems { get; init; }
  public decimal TotalAmount { get; init; }
  public DateTime CreatedAt { get; init; }
}
