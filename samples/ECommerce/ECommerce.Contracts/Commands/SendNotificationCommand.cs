using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to send a notification to a customer
/// </summary>
public record SendNotificationCommand : ICommand {
  public required string CustomerId { get; init; }
  public required string Subject { get; init; }
  public required string Message { get; init; }
  public NotificationType Type { get; init; }
}

public enum NotificationType {
  Email,
  Sms,
  Push
}
