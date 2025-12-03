using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.OrderService.API.Receptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace ECommerce.OrderService.Tests;

/// <summary>
/// Unit tests for CreateOrderReceptor2 (outbox pattern implementation)
/// </summary>
public class CreateOrderReceptor2Tests {
  /// <summary>
  /// Helper class to track outbox calls
  /// </summary>
  private class TestOutbox : IOutbox {
    public List<(IMessageEnvelope envelope, string destination)> StoredMessages { get; } = [];
    public int StoreCount => StoredMessages.Count;

    public Task<OutboxMessage> StoreAsync<TMessage>(MessageEnvelope<TMessage> envelope, string destination, CancellationToken cancellationToken = default) {
      StoredMessages.Add((envelope, destination));
      var outboxMessage = new OutboxMessage(
        envelope.MessageId,
        destination,
        typeof(TMessage).FullName ?? typeof(TMessage).Name,
        "{}",  // Dummy JSON
        "{}",  // Dummy metadata
        null,  // No scope
        DateTimeOffset.UtcNow
      );
      return Task.FromResult(outboxMessage);
    }

    public Task<OutboxMessage> StoreAsync(IMessageEnvelope envelope, string destination, CancellationToken cancellationToken = default) {
      StoredMessages.Add((envelope, destination));
      var outboxMessage = new OutboxMessage(
        envelope.MessageId,
        destination,
        envelope.GetType().FullName ?? envelope.GetType().Name,
        "{}",  // Dummy JSON
        "{}",  // Dummy metadata
        null,  // No scope
        DateTimeOffset.UtcNow
      );
      return Task.FromResult(outboxMessage);
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task MarkPublishedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }
  }

  [Test]
  public async Task CreateOrderReceptor2_ValidOrder_StoresEventInOutboxAsync() {
    // Arrange
    var outbox = new TestOutbox();
    var logger = NullLogger<CreateOrderReceptor2>.Instance;
    var receptor = new CreateOrderReceptor2(outbox, logger);

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

    // Verify StoreAsync was called with correct destination
    await Assert.That(outbox.StoreCount).IsEqualTo(1);
    await Assert.That(outbox.StoredMessages[0].destination).IsEqualTo("orders/created");
  }

  [Test]
  public async Task CreateOrderReceptor2_NegativeTotalAmount_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var outbox = new TestOutbox();
    var logger = NullLogger<CreateOrderReceptor2>.Instance;
    var receptor = new CreateOrderReceptor2(outbox, logger);

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
  public async Task CreateOrderReceptor2_ZeroTotalAmount_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var outbox = new TestOutbox();
    var logger = NullLogger<CreateOrderReceptor2>.Instance;
    var receptor = new CreateOrderReceptor2(outbox, logger);

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
  public async Task CreateOrderReceptor2_EmptyLineItems_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var outbox = new TestOutbox();
    var logger = NullLogger<CreateOrderReceptor2>.Instance;
    var receptor = new CreateOrderReceptor2(outbox, logger);

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
  public async Task CreateOrderReceptor2_ValidOrder_MapsAllPropertiesCorrectlyAsync() {
    // Arrange
    var outbox = new TestOutbox();
    var logger = NullLogger<CreateOrderReceptor2>.Instance;
    var receptor = new CreateOrderReceptor2(outbox, logger);

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

    // Verify outbox StoreAsync was called
    await Assert.That(outbox.StoreCount).IsEqualTo(1);
  }
}
