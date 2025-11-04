using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.OrderService.API.Services;

/// <summary>
/// Background service that makes EXPLICIT dispatcher calls
/// Demonstrates dispatchers outside of receptors/perspectives
/// </summary>
public class OrderRetryService {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<OrderRetryService> _logger;

  public OrderRetryService(IDispatcher dispatcher, ILogger<OrderRetryService> _logger) {
    _dispatcher = dispatcher;
    this._logger = _logger;
  }

  /// <summary>
  /// Retries a failed order by explicitly dispatching CreateOrderCommand
  /// </summary>
  public async Task RetryFailedOrderAsync(string orderId, string customerId) {
    _logger.LogInformation(
      "Retrying failed order {OrderId} for customer {CustomerId}",
      orderId,
      customerId);

    // Explicitly dispatch CreateOrderCommand
    var command = new CreateOrderCommand {
      OrderId = orderId,
      CustomerId = customerId,
      LineItems = new List<OrderLineItem> {
        new() {
          ProductId = "PROD-001",
          ProductName = "Test Product",
          Quantity = 2,
          UnitPrice = 29.99m
        }
      },
      TotalAmount = 59.98m
    };

    await _dispatcher.SendAsync<OrderCreatedEvent>(command);

    _logger.LogInformation(
      "Retry command dispatched for order {OrderId}",
      orderId);
  }

  /// <summary>
  /// Explicitly publishes a test notification command
  /// </summary>
  public async Task SendTestNotificationAsync(string customerId, string subject, string message) {
    _logger.LogInformation(
      "Sending test notification for customer {CustomerId}",
      customerId);

    // Explicitly dispatch SendNotificationCommand
    var command = new SendNotificationCommand {
      CustomerId = customerId,
      Subject = subject,
      Message = message,
      Type = NotificationType.Email
    };

    await _dispatcher.SendAsync<NotificationSentEvent>(command);

    _logger.LogInformation(
      "Test notification command dispatched for customer {CustomerId}",
      customerId);
  }

  /// <summary>
  /// Publishes a test event explicitly
  /// </summary>
  public async Task PublishTestEventAsync(string orderId) {
    _logger.LogInformation(
      "Publishing test OrderCreatedEvent for order {OrderId}",
      orderId);

    // Explicitly publish OrderCreatedEvent
    var @event = new OrderCreatedEvent {
      OrderId = orderId,
      CustomerId = "CUST-TEST",
      CreatedAt = DateTime.UtcNow,
      LineItems = new List<OrderLineItem> {
        new() {
          ProductId = "PROD-TEST",
          ProductName = "Test Product",
          Quantity = 1,
          UnitPrice = 9.99m
        }
      },
      TotalAmount = 9.99m
    };

    await _dispatcher.PublishAsync(@event);

    _logger.LogInformation(
      "Test event published for order {OrderId}",
      orderId);
  }
}
