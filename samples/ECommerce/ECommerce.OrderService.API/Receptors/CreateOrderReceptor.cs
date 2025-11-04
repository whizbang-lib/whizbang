using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.OrderService.API.Receptors;

/// <summary>
/// Handles CreateOrderCommand and publishes OrderCreatedEvent
/// </summary>
public class CreateOrderReceptor : IReceptor<CreateOrderCommand, OrderCreatedEvent> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<CreateOrderReceptor> _logger;

  public CreateOrderReceptor(IDispatcher dispatcher, ILogger<CreateOrderReceptor> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async Task<OrderCreatedEvent> HandleAsync(
    CreateOrderCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Processing order {OrderId} for customer {CustomerId} with {ItemCount} items",
      message.OrderId,
      message.CustomerId,
      message.LineItems.Count);

    // Validate order (business logic would go here)
    if (message.TotalAmount <= 0) {
      throw new InvalidOperationException("Order total must be positive");
    }

    if (message.LineItems.Count == 0) {
      throw new InvalidOperationException("Order must contain at least one item");
    }

    // Create the event
    var orderCreated = new OrderCreatedEvent {
      OrderId = message.OrderId,
      CustomerId = message.CustomerId,
      LineItems = message.LineItems,
      TotalAmount = message.TotalAmount,
      CreatedAt = DateTime.UtcNow
    };

    // Publish the event
    await _dispatcher.PublishAsync(orderCreated);

    _logger.LogInformation("Order {OrderId} created successfully", message.OrderId);

    return orderCreated;
  }
}


/// <summary>
/// Handles CreateOrderCommand and publishes OrderCreatedEvent
/// </summary>
public class CreateOrderReceptor2 : IReceptor<CreateOrderCommand, OrderCreatedEvent> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<CreateOrderReceptor> _logger;

  public CreateOrderReceptor2(IDispatcher dispatcher, ILogger<CreateOrderReceptor> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async Task<OrderCreatedEvent> HandleAsync(
    CreateOrderCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Processing order {OrderId} for customer {CustomerId} with {ItemCount} items",
      message.OrderId,
      message.CustomerId,
      message.LineItems.Count);

    // Validate order (business logic would go here)
    if (message.TotalAmount <= 0) {
      throw new InvalidOperationException("Order total must be positive");
    }

    if (message.LineItems.Count == 0) {
      throw new InvalidOperationException("Order must contain at least one item");
    }

    // Create the event
    var orderCreated = new OrderCreatedEvent {
      OrderId = message.OrderId,
      CustomerId = message.CustomerId,
      LineItems = message.LineItems,
      TotalAmount = message.TotalAmount,
      CreatedAt = DateTime.UtcNow
    };

    // Publish the event
    await _dispatcher.PublishAsync(orderCreated);

    _logger.LogInformation("Order {OrderId} created successfully", message.OrderId);

    return orderCreated;
  }
}
