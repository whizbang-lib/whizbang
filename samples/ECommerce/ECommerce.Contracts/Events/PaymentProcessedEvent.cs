using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when payment is successfully processed
/// </summary>
[PinnedId("243a75d6-4fa3-419e-a56b-c0f393bd043c")]
public record PaymentProcessedEvent : IEvent {
  [StreamId]
  public required string OrderId { get; init; }
  public required string CustomerId { get; init; }
  public decimal Amount { get; init; }
  public required string TransactionId { get; init; }
}
