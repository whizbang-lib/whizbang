using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.NotificationWorker.Receptors;

/// <summary>
/// Handles SendNotificationCommand and publishes NotificationSentEvent
/// </summary>
public class SendNotificationReceptor : IReceptor<SendNotificationCommand, NotificationSentEvent> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<SendNotificationReceptor> _logger;

  public SendNotificationReceptor(IDispatcher dispatcher, ILogger<SendNotificationReceptor> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async ValueTask<NotificationSentEvent> HandleAsync(
    SendNotificationCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Sending {NotificationType} notification to customer {CustomerId}: {Subject}",
      message.Type,
      message.CustomerId,
      message.Subject);

    // Send the notification (business logic would go here)
    // In a real system, this would call an email/SMS/push notification service

    // Simulate sending delay
    await Task.Delay(100, cancellationToken);

    var notificationSent = new NotificationSentEvent {
      CustomerId = message.CustomerId,
      Subject = message.Subject,
      Type = message.Type,
      SentAt = DateTime.UtcNow
    };

    // Publish the event
    //
    await _dispatcher.PublishAsync(notificationSent);

    _logger.LogInformation(
      "Notification sent to customer {CustomerId}",
      message.CustomerId);

    return notificationSent;
  }
}
