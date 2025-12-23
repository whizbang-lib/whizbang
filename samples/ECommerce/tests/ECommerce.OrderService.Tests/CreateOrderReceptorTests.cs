using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.OrderService.API.Receptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.OrderService.Tests;

/// <summary>
/// Unit tests for CreateOrderReceptor
/// </summary>
public class CreateOrderReceptorTests {
  /// <summary>
  /// Helper class to track dispatcher calls
  /// </summary>
  private class TestDispatcher : IDispatcher {
    public List<object> PublishedMessages { get; } = [];
    public int PublishCount => PublishedMessages.Count;

    public Task PublishAsync<TEvent>(TEvent @event) {
      PublishedMessages.Add(@event!);
      return Task.CompletedTask;
    }

    // Generic SendAsync methods
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) => throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();

    // Non-generic SendAsync methods
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotImplementedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();

    // Generic LocalInvokeAsync methods with result
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();

    // Non-generic LocalInvokeAsync methods with result
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotImplementedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();

    // Generic LocalInvokeAsync methods without result
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();

    // Non-generic LocalInvokeAsync methods without result
    public ValueTask LocalInvokeAsync(object message) => throw new NotImplementedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotImplementedException();

    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotImplementedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotImplementedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotImplementedException();
  }

  [Test]
  [Obsolete]
  public async Task CreateOrderReceptor_ValidOrder_PublishesEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = NullLogger<CreateOrderReceptor>.Instance;
    var receptor = new CreateOrderReceptor(dispatcher, logger);

    var orderId = OrderId.New();
    var customerId = CustomerId.New();
    var productId = ProductId.New();
    var command = new CreateOrderCommand {
      OrderId = orderId,
      CustomerId = customerId,
      LineItems = [
        new OrderLineItem {
          ProductId = productId,
          ProductName = "Widget",
          Quantity = 2,
          UnitPrice = 19.99m
        }
      ],
      TotalAmount = 39.98m
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OrderId).IsEqualTo(command.OrderId);
    await Assert.That(result.CustomerId).IsEqualTo(command.CustomerId);
    await Assert.That(result.LineItems).HasCount().EqualTo(1);
    await Assert.That(result.TotalAmount).IsEqualTo(39.98m);

    // Verify PublishAsync was called
    await Assert.That(dispatcher.PublishCount).IsEqualTo(1);
    await Assert.That(dispatcher.PublishedMessages[0]).IsTypeOf<OrderCreatedEvent>();
  }

  [Test]
  public async Task CreateOrderReceptor_NegativeTotalAmount_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = NullLogger<CreateOrderReceptor>.Instance;
    var receptor = new CreateOrderReceptor(dispatcher, logger);

    var command = new CreateOrderCommand {
      OrderId = OrderId.New(),
      CustomerId = CustomerId.New(),
      LineItems = [
        new OrderLineItem {
          ProductId = ProductId.New(),
          ProductName = "Widget",
          Quantity = 2,
          UnitPrice = 19.99m
        }
      ],
      TotalAmount = -10.00m  // Invalid negative amount
    };

    // Act & Assert
    await Assert.That(async () => await receptor.HandleAsync(command))
      .Throws<InvalidOperationException>()
      .WithMessage("Order total must be positive");
  }

  [Test]
  public async Task CreateOrderReceptor_ZeroTotalAmount_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = NullLogger<CreateOrderReceptor>.Instance;
    var receptor = new CreateOrderReceptor(dispatcher, logger);

    var command = new CreateOrderCommand {
      OrderId = OrderId.New(),
      CustomerId = CustomerId.New(),
      LineItems = [
        new OrderLineItem {
          ProductId = ProductId.New(),
          ProductName = "Widget",
          Quantity = 2,
          UnitPrice = 19.99m
        }
      ],
      TotalAmount = 0.00m  // Invalid zero amount
    };

    // Act & Assert
    await Assert.That(async () => await receptor.HandleAsync(command))
      .Throws<InvalidOperationException>()
      .WithMessage("Order total must be positive");
  }

  [Test]
  public async Task CreateOrderReceptor_EmptyLineItems_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = NullLogger<CreateOrderReceptor>.Instance;
    var receptor = new CreateOrderReceptor(dispatcher, logger);

    var command = new CreateOrderCommand {
      OrderId = OrderId.New(),
      CustomerId = CustomerId.New(),
      LineItems = [],  // Empty list
      TotalAmount = 39.98m
    };

    // Act & Assert
    await Assert.That(async () => await receptor.HandleAsync(command))
      .Throws<InvalidOperationException>()
      .WithMessage("Order must contain at least one item");
  }

  [Test]
  [Obsolete]
  public async Task CreateOrderReceptor_ValidOrder_MapsAllPropertiesCorrectlyAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = NullLogger<CreateOrderReceptor>.Instance;
    var receptor = new CreateOrderReceptor(dispatcher, logger);

    var orderId = OrderId.New();
    var customerId = CustomerId.New();
    var productId1 = ProductId.New();
    var productId2 = ProductId.New();
    var command = new CreateOrderCommand {
      OrderId = orderId,
      CustomerId = customerId,
      LineItems = [
        new OrderLineItem {
          ProductId = productId1,
          ProductName = "Widget",
          Quantity = 2,
          UnitPrice = 19.99m
        },
        new OrderLineItem {
          ProductId = productId2,
          ProductName = "Gadget",
          Quantity = 1,
          UnitPrice = 29.99m
        }
      ],
      TotalAmount = 69.97m
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result.OrderId).IsEqualTo(orderId);
    await Assert.That(result.CustomerId).IsEqualTo(customerId);
    await Assert.That(result.LineItems).HasCount().EqualTo(2);
    await Assert.That(result.TotalAmount).IsEqualTo(69.97m);
    await Assert.That(result.CreatedAt).IsGreaterThan(DateTime.UtcNow.AddSeconds(-5));

    // Verify first line item
    var firstItem = result.LineItems[0];
    await Assert.That(firstItem.ProductId).IsEqualTo(productId1);
    await Assert.That(firstItem.ProductName).IsEqualTo("Widget");
    await Assert.That(firstItem.Quantity).IsEqualTo(2);
    await Assert.That(firstItem.UnitPrice).IsEqualTo(19.99m);

    // Verify second line item
    var secondItem = result.LineItems[1];
    await Assert.That(secondItem.ProductId).IsEqualTo(productId2);
    await Assert.That(secondItem.ProductName).IsEqualTo("Gadget");
    await Assert.That(secondItem.Quantity).IsEqualTo(1);
    await Assert.That(secondItem.UnitPrice).IsEqualTo(29.99m);
  }
}
