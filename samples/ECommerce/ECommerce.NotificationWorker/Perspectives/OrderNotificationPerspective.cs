using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.NotificationWorker.Perspectives;

/// <summary>
/// Listens to OrderCreatedEvent and dispatches SendNotificationCommand
/// </summary>
public class OrderNotificationPerspective : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<OrderNotificationPerspective> _logger;

  public OrderNotificationPerspective(IDispatcher dispatcher, ILogger<OrderNotificationPerspective> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    _logger.LogInformation(
      "Sending order confirmation notification for order {OrderId} to customer {CustomerId}",
      @event.OrderId,
      @event.CustomerId);

    var notificationCommand = new SendNotificationCommand {
      CustomerId = @event.CustomerId,
      Subject = $"Order Confirmation - Order #{@event.OrderId}",
      Message = $"Thank you for your order! Your order #{@event.OrderId} totaling ${@event.TotalAmount:F2} has been received and is being processed.",
      Type = NotificationType.Email
    };

    await _dispatcher.SendAsync<NotificationSentEvent>(notificationCommand);

    _logger.LogInformation(
      "Order confirmation notification dispatched for order {OrderId}",
      @event.OrderId);
  }
}
