using ECommerce.Contracts.Commands;
using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when a notification is successfully sent
/// </summary>
[PinnedId("98fa1a45-cadb-4891-a99b-85cb810ef6ea")]
public record NotificationSentEvent : IEvent {
  [StreamId]
  public required string CustomerId { get; init; }
  public required string Subject { get; init; }
  public NotificationType Type { get; init; }
  public DateTime SentAt { get; init; }
}
