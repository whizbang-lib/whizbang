using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Whizbang.Core;

namespace ECommerce.NotificationWorker.Perspectives;

/// <summary>
/// Listens to OrderCreatedEvent and dispatches SendNotificationCommand
/// </summary>
public class OrderNotificationPerspective(IDispatcher dispatcher, ILogger<OrderNotificationPerspective> logger) : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IDispatcher _dispatcher = dispatcher;
  private readonly ILogger<OrderNotificationPerspective> _logger = logger;

  public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    _logger.LogInformation(
      "Sending order confirmation notification for order {OrderId} to customer {CustomerId}",
      @event.OrderId,
      @event.CustomerId);

    var notificationCommand = new SendNotificationCommand {
      CustomerId = @event.CustomerId.Value.ToString(),
      Subject = $"Order Confirmation - Order #{@event.OrderId.Value}",
      Message = $"Thank you for your order! Your order #{@event.OrderId.Value} totaling ${@event.TotalAmount:F2} has been received and is being processed.",
      Type = NotificationType.Email
    };

    await _dispatcher.SendAsync(notificationCommand);

    _logger.LogInformation(
      "Order confirmation notification dispatched for order {OrderId}",
      @event.OrderId);
  }
}
