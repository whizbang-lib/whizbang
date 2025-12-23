using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Tests.Common;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Receptors;

/// <summary>
/// Tests for v0.1.0 Receptor functionality.
/// These tests define the required behavior for stateless receptors.
/// </summary>
[Category("Receptors")]
public class ReceptorTests : DiagnosticTestBase {
  protected override DiagnosticCategory DiagnosticCategories => DiagnosticCategory.ReceptorDiscovery;

  // Test Messages
  public record CreateOrder(Guid CustomerId, OrderItem[] Items);
  public record OrderItem(string Sku, int Quantity, decimal Price);
  public record OrderCreated(Guid OrderId, Guid CustomerId, OrderItem[] Items, decimal Total);
  public record UpdateOrder(Guid OrderId, string[] Changes);
  public record OrderUpdated(Guid OrderId, string[] Changes);
  public record CancelOrder(Guid OrderId);
  public record OrderCancelled(Guid OrderId);

  // Test receptor implementations (will be created when implementing)
  public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    public async ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken cancellationToken = default) {
      // Validation
      if (message.Items.Length == 0) {
        throw new InvalidOperationException("Order must have items");
      }

      // Introduce async delay to ensure task is not immediately completed
      await Task.Delay(1, cancellationToken);

      // Calculate total
      var total = message.Items.Sum(item => item.Quantity * item.Price);

      // Return event
      return new OrderCreated(
          OrderId: Guid.NewGuid(),
          CustomerId: message.CustomerId,
          Items: message.Items,
          Total: total
      );
    }
  }

  [Test]
  public async Task Receive_ValidCommand_ShouldReturnTypeSafeResponseAsync() {
    // Arrange
    var receptor = new OrderReceptor();
    var command = new CreateOrder(
        CustomerId: Guid.NewGuid(),
        Items: [new OrderItem("SKU-001", 2, 29.99m)]
    );

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result).IsTypeOf<OrderCreated>();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
    await Assert.That(result.Total).IsEqualTo(59.98m);
  }

  [Test]
  public async Task Receive_EmptyItems_ShouldThrowExceptionAsync() {
    // Arrange
    var receptor = new OrderReceptor();
    var command = new CreateOrder(
        CustomerId: Guid.NewGuid(),
        Items: []
    );

    // Act & Assert
    await Assert.That(async () => await receptor.HandleAsync(command))
        .ThrowsExactly<InvalidOperationException>()
        .WithMessage("Order must have items");
  }

  [Test]
  public async Task Receive_AsyncOperation_ShouldCompleteAsynchronouslyAsync() {
    // Arrange
    var receptor = new OrderReceptor();
    var command = new CreateOrder(
        CustomerId: Guid.NewGuid(),
        Items: [new OrderItem("SKU-001", 1, 10.00m)]
    );

    // Act
    var task = receptor.HandleAsync(command);
    await Assert.That(task.IsCompleted).IsFalse(); // Should be async
    var result = await task;

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task Receive_CalculatesTotal_ShouldSumItemPricesAsync() {
    // Arrange
    var receptor = new OrderReceptor();
    var command = new CreateOrder(
        CustomerId: Guid.NewGuid(),
        Items: [
                new OrderItem("SKU-001", 2, 10.00m),
                new OrderItem("SKU-002", 3, 15.00m)
        ]
    );

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result.Total).IsEqualTo(65.00m); // (2 * 10) + (3 * 15)
  }

  [Test]
  public async Task Receptor_ShouldBeStateless_NoPersistentStateAsync() {
    // Arrange
    var receptor = new OrderReceptor();
    var command1 = new CreateOrder(
        CustomerId: Guid.NewGuid(),
        Items: [new OrderItem("SKU-001", 1, 10.00m)]
    );
    var command2 = new CreateOrder(
        CustomerId: Guid.NewGuid(),
        Items: [new OrderItem("SKU-002", 2, 20.00m)]
    );

    // Act
    var result1 = await receptor.HandleAsync(command1);
    var result2 = await receptor.HandleAsync(command2);

    // Assert - Each call should be independent
    await Assert.That(result1.OrderId).IsNotEqualTo(result2.OrderId);
    await Assert.That(result1.Total).IsEqualTo(10.00m);
    await Assert.That(result2.Total).IsEqualTo(40.00m);
  }

  [Test]
  public async Task MultipleReceptors_SameMessageType_ShouldAllHandleAsync() {
    // This tests multi-destination routing
    // Multiple receptors can handle the same message type
    // This will be implemented at the Dispatcher level

    // Arrange
    var businessReceptor = new OrderBusinessReceptor();
    var auditReceptor = new OrderAuditReceptor();
    var command = new CreateOrder(
        CustomerId: Guid.NewGuid(),
        Items: [new OrderItem("SKU-001", 1, 10.00m)]
    );

    // Act
    var businessResult = await businessReceptor.HandleAsync(command);
    var auditResult = await auditReceptor.HandleAsync(command);

    // Assert
    await Assert.That(businessResult).IsTypeOf<OrderCreated>();
    await Assert.That(auditResult).IsTypeOf<AuditEvent>();
  }

  [Test]
  public async Task Receptor_TupleResponse_ShouldReturnMultipleEventsAsync() {
    // Test flexible response type: tuple
    var receptor = new PaymentReceptor();
    var command = new ProcessPayment(Guid.NewGuid(), 100.00m);

    // Act
    var (payment, audit) = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(payment).IsTypeOf<PaymentProcessed>();
    await Assert.That(audit).IsTypeOf<AuditEvent>();
  }

  [Test]
  public async Task Receptor_ArrayResponse_ShouldReturnDynamicNumberOfEventsAsync() {
    // Test flexible response type: array
    var receptor = new NotificationReceptor();
    var orderCreated = new OrderCreated(
        Guid.NewGuid(),
        Guid.NewGuid(),
        [new OrderItem("SKU-001", 10, 100.00m)],
        1000.00m
    );

    // Act
    var notifications = await receptor.HandleAsync(orderCreated);

    // Assert
    await Assert.That(notifications.Length).IsGreaterThan(0);
    await Assert.That(notifications.Any(n => n is EmailSent)).IsTrue();
    await Assert.That(notifications.Any(n => n is HighValueAlert)).IsTrue(); // High value order
  }

  // Supporting types for multi-destination test
  public record AuditEvent(string Action, Guid EntityId);

  public class OrderBusinessReceptor : IReceptor<CreateOrder, OrderCreated> {
    public async ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken cancellationToken = default) {
      await Task.Delay(1, cancellationToken);

      var total = message.Items.Sum(item => item.Quantity * item.Price);

      return new OrderCreated(
          OrderId: Guid.NewGuid(),
          CustomerId: message.CustomerId,
          Items: message.Items,
          Total: total
      );
    }
  }

  public class OrderAuditReceptor : IReceptor<CreateOrder, AuditEvent> {
    public async ValueTask<AuditEvent> HandleAsync(CreateOrder message, CancellationToken cancellationToken = default) {
      await Task.Delay(1, cancellationToken);

      return new AuditEvent(
          Action: "OrderCreated",
          EntityId: message.CustomerId
      );
    }
  }

  // Supporting types for tuple response test
  public record ProcessPayment(Guid PaymentId, decimal Amount);
  public record PaymentProcessed(Guid PaymentId, decimal Amount);

  public class PaymentReceptor : IReceptor<ProcessPayment, (PaymentProcessed, AuditEvent)> {
    public async ValueTask<(PaymentProcessed, AuditEvent)> HandleAsync(ProcessPayment message, CancellationToken cancellationToken = default) {
      await Task.Delay(1, cancellationToken);

      var payment = new PaymentProcessed(
          PaymentId: message.PaymentId,
          Amount: message.Amount
      );

      var audit = new AuditEvent(
          Action: "PaymentProcessed",
          EntityId: message.PaymentId
      );

      return (payment, audit);
    }
  }

  // Supporting types for array response test
  public interface INotificationEvent { }
  public record EmailSent(Guid CustomerId) : INotificationEvent;
  public record HighValueAlert(Guid OrderId) : INotificationEvent;

  public class NotificationReceptor : IReceptor<OrderCreated, INotificationEvent[]> {
    public async ValueTask<INotificationEvent[]> HandleAsync(OrderCreated message, CancellationToken cancellationToken = default) {
      await Task.Delay(1, cancellationToken);

      var notifications = new List<INotificationEvent> {
        // Always send email
        new EmailSent(message.CustomerId)
      };

      // High value alert for orders >= $1000
      if (message.Total >= 1000m) {
        notifications.Add(new HighValueAlert(message.OrderId));
      }

      return [.. notifications];
    }
  }
}
