using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when payment processing fails
/// </summary>
public record PaymentFailedEvent : IEvent {
  [StreamKey]
  public required string OrderId { get; init; }
  public required string CustomerId { get; init; }
  public required string Reason { get; init; }
}
