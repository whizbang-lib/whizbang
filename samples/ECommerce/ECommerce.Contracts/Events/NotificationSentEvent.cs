using Whizbang.Core;
using ECommerce.Contracts.Commands;

namespace ECommerce.Contracts.Events;

/// <summary>
/// Event published when a notification is successfully sent
/// </summary>
public record NotificationSentEvent : IEvent {
  public required string CustomerId { get; init; }
  public required string Subject { get; init; }
  public NotificationType Type { get; init; }
  public DateTime SentAt { get; init; }
}
