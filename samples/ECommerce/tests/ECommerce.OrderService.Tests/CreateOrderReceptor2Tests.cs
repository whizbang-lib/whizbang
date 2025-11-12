using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.OrderService.API.Receptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Messaging;
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
    public List<(MessageId messageId, string topic, byte[] payload)> StoredMessages { get; } = new();
    public int StoreCount => StoredMessages.Count;

    public Task StoreAsync(MessageId messageId, string topic, byte[] payload, CancellationToken cancellationToken = default) {
      StoredMessages.Add((messageId, topic, payload));
      return Task.CompletedTask;
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

    var command = new CreateOrderCommand {
      OrderId = Guid.NewGuid().ToString(),
      CustomerId = "CUST-001",
      LineItems = new List<OrderLineItem> {
        new OrderLineItem {
          ProductId = "PROD-001",
          ProductName = "Widget",
          Quantity = 2,
          UnitPrice = 19.99m
        }
      },
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

    // Verify StoreAsync was called with correct topic
    await Assert.That(outbox.StoreCount).IsEqualTo(1);
    await Assert.That(outbox.StoredMessages[0].topic).IsEqualTo("orders/created");
  }

  [Test]
  public async Task CreateOrderReceptor2_NegativeTotalAmount_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var outbox = new TestOutbox();
    var logger = NullLogger<CreateOrderReceptor2>.Instance;
    var receptor = new CreateOrderReceptor2(outbox, logger);

    var command = new CreateOrderCommand {
      OrderId = Guid.NewGuid().ToString(),
      CustomerId = "CUST-001",
      LineItems = new List<OrderLineItem> {
        new OrderLineItem {
          ProductId = "PROD-001",
          ProductName = "Widget",
          Quantity = 2,
          UnitPrice = 19.99m
        }
      },
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
      OrderId = Guid.NewGuid().ToString(),
      CustomerId = "CUST-001",
      LineItems = new List<OrderLineItem> {
        new OrderLineItem {
          ProductId = "PROD-001",
          ProductName = "Widget",
          Quantity = 2,
          UnitPrice = 19.99m
        }
      },
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
      OrderId = Guid.NewGuid().ToString(),
      CustomerId = "CUST-001",
      LineItems = new List<OrderLineItem>(),  // Empty list
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

    var orderId = Guid.NewGuid().ToString();
    var customerId = "CUST-123";
    var command = new CreateOrderCommand {
      OrderId = orderId,
      CustomerId = customerId,
      LineItems = new List<OrderLineItem> {
        new OrderLineItem {
          ProductId = "PROD-001",
          ProductName = "Widget",
          Quantity = 2,
          UnitPrice = 19.99m
        },
        new OrderLineItem {
          ProductId = "PROD-002",
          ProductName = "Gadget",
          Quantity = 1,
          UnitPrice = 29.99m
        }
      },
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
