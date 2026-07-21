using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when payment processing fails
/// </summary>
[PinnedId("61dbb573-ac4a-43ea-9f2f-b8887b5fda81")]
public record PaymentFailedEvent : IEvent {
  [StreamId]
  public required string OrderId { get; init; }
  public required string CustomerId { get; init; }
  public required string Reason { get; init; }
}
