using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Contracts.Generated;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace ECommerce.OrderService.API.Receptors;

/// <summary>
/// Handles CreateOrderCommand and publishes OrderCreatedEvent
/// </summary>
public class CreateOrderReceptor(IDispatcher dispatcher, ILogger<CreateOrderReceptor> logger) : IReceptor<CreateOrderCommand, OrderCreatedEvent> {
  private readonly IDispatcher _dispatcher = dispatcher;
  private readonly ILogger<CreateOrderReceptor> _logger = logger;

  public async ValueTask<OrderCreatedEvent> HandleAsync(
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

    // Publish the event for cross-service delivery
    // This will be sent to Azure Service Bus and consumed by other services
    await _dispatcher.PublishAsync(orderCreated);

    _logger.LogInformation("Order {OrderId} created and event published", message.OrderId);

    return orderCreated;
  }
}


/// <summary>
/// Handles CreateOrderCommand and publishes OrderCreatedEvent (alternative implementation for testing)
/// </summary>
public class CreateOrderReceptor2(IOutbox outbox, ILogger<CreateOrderReceptor2> logger) : IReceptor<CreateOrderCommand, OrderCreatedEvent> {
  private readonly IOutbox _outbox = outbox;
  private readonly ILogger<CreateOrderReceptor2> _logger = logger;

  public async ValueTask<OrderCreatedEvent> HandleAsync(
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

    // Publish the event to the outbox for reliable cross-service delivery
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = orderCreated,
      Hops = []
    };
    await _outbox.StoreAsync(envelope, "orders/created", cancellationToken);

    _logger.LogInformation("Order {OrderId} created and event stored in outbox (Receptor2)", message.OrderId);

    return orderCreated;
  }
}
