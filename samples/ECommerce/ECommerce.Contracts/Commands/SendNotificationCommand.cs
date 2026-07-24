using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to send a notification to a customer
/// </summary>
[PinnedId("f43f4cb0-bad1-4d4d-807b-3c32963240f6")]
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
