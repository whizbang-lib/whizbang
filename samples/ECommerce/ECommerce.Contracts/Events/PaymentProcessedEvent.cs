using Whizbang.Core;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when payment is successfully processed
/// </summary>
public record PaymentProcessedEvent : IEvent {
  [StreamKey]
  public required string OrderId { get; init; }
  public required string CustomerId { get; init; }
  public decimal Amount { get; init; }
  public required string TransactionId { get; init; }
}
