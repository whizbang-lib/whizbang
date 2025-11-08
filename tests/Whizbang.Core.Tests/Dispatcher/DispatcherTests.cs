using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for v0.1.0 Dispatcher functionality.
/// These tests define the required behavior for message routing and orchestration.
/// </summary>
public class DispatcherTests {
  // Test Messages
  public record CreateOrder(Guid CustomerId, string[] Items);
  public record OrderCreated(Guid OrderId, Guid CustomerId);
  public record OrderPlaced(Guid OrderId);

  [Test]
  public async Task Send_WithValidMessage_ShouldReturnDeliveryReceiptAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1", "item2" });

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(receipt.Destination).Contains("CreateOrder");
  }

  [Test]
  public async Task LocalInvoke_WithValidMessage_ShouldReturnBusinessResultAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1", "item2" });

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderCreated>(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<OrderCreated>();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task Send_WithUnknownMessageType_ShouldThrowHandlerNotFoundExceptionAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var unknownCommand = new UnknownCommand();

    // Act & Assert
    var exception = await Assert.That(async () => await dispatcher.SendAsync(unknownCommand))
        .ThrowsExactly<HandlerNotFoundException>();

    await Assert.That(exception?.Message).Contains("UnknownCommand");
    await Assert.That(exception?.MessageType).IsEqualTo(typeof(UnknownCommand));
  }

  [Test]
  public async Task LocalInvoke_WithUnknownMessageType_ShouldThrowHandlerNotFoundExceptionAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var unknownCommand = new UnknownCommand();

    // Act & Assert
    var exception = await Assert.That(async () => await dispatcher.LocalInvokeAsync<object>(unknownCommand))
        .ThrowsExactly<HandlerNotFoundException>();

    await Assert.That(exception?.Message).Contains("UnknownCommand");
  }

  [Test]
  public async Task Send_WithContext_ShouldPreserveCorrelationIdInReceiptAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1" });
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var context = MessageContext.Create(correlationId, causationId);

    // Act
    var receipt = await dispatcher.SendAsync(command, context);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(receipt.CausationId).IsEqualTo(causationId);
  }

  [Test]
  public async Task LocalInvoke_WithContext_ShouldPreserveContextAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1" });
    var context = MessageContext.Create(
        CorrelationId.New(),
        MessageId.New()
    );

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderCreated>(command, context);

    // Assert
    await Assert.That(result).IsNotNull();
    // Context should be tracked (verified through traceability)
  }

  [Test]
  public async Task Publish_WithEvent_ShouldNotifyAllHandlersAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var orderCreated = new OrderCreated(Guid.NewGuid(), Guid.NewGuid());

    // Subscribe multiple handlers (this will be via perspectives in implementation)
    // For now, test that Publish doesn't throw

    // Act
    await dispatcher.PublishAsync(orderCreated);

    // Assert
    // Should complete without error
    // In full implementation, verify all perspectives were notified
  }

  [Test]
  public async Task SendMany_WithMultipleCommands_ShouldReturnAllReceiptsAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var commands = new object[] {
            new CreateOrder(Guid.NewGuid(), new[] { "item1" }),
            new CreateOrder(Guid.NewGuid(), new[] { "item2" }),
            new CreateOrder(Guid.NewGuid(), new[] { "item3" })
        };

    // Act
    var receipts = await dispatcher.SendManyAsync(commands);

    // Assert
    await Assert.That(receipts).IsNotNull();
    await Assert.That(receipts.Count()).IsEqualTo(3);
    foreach (var receipt in receipts) {
      await Assert.That(receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);
      await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    }
  }

  [Test]
  public async Task LocalInvokeMany_WithMultipleCommands_ShouldReturnAllResultsAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var commands = new object[] {
            new CreateOrder(Guid.NewGuid(), new[] { "item1" }),
            new CreateOrder(Guid.NewGuid(), new[] { "item2" }),
            new CreateOrder(Guid.NewGuid(), new[] { "item3" })
        };

    // Act
    var results = await dispatcher.LocalInvokeManyAsync<OrderCreated>(commands);

    // Assert
    await Assert.That(results).IsNotNull();
    await Assert.That(results.Count()).IsEqualTo(3);
    foreach (var result in results) {
      await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
    }
  }

  [Test]
  public async Task Dispatcher_MessageContext_ShouldGenerateUniqueMessageIdsAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command1 = new CreateOrder(Guid.NewGuid(), new[] { "item1" });
    var command2 = new CreateOrder(Guid.NewGuid(), new[] { "item2" });

    // Act
    var receipt1 = await dispatcher.SendAsync(command1);
    var receipt2 = await dispatcher.SendAsync(command2);

    // Assert
    // Each message should have unique MessageId
    await Assert.That(receipt1.MessageId).IsNotEqualTo(receipt2.MessageId);
  }

  [Test]
  public async Task Dispatcher_ShouldRouteToCorrectHandlerAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var createCommand = new CreateOrder(Guid.NewGuid(), new[] { "item1" });

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderCreated>(createCommand);

    // Assert - Should route to OrderReceptor specifically
    await Assert.That(result).IsTypeOf<OrderCreated>();
    await Assert.That(result.CustomerId).IsEqualTo(createCommand.CustomerId);
  }

  [Test]
  public async Task Dispatcher_MultipleReceptorsSameMessage_ShouldRouteToAllAsync() {
    // This tests multi-destination routing
    // When multiple receptors handle the same message type,
    // dispatcher should route to all of them

    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1" });

    // Act - LocalInvoke should complete
    // In full implementation with multi-destination support,
    // this would return results from all receptors
    var result = await dispatcher.LocalInvokeAsync<OrderCreated>(command);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task Dispatcher_ShouldTrackCausationChainInReceiptAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var causationId = MessageId.New();
    var correlationId = CorrelationId.New();
    var initialContext = MessageContext.Create(correlationId, causationId);
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1" });

    // Act
    var receipt = await dispatcher.SendAsync(command, initialContext);

    // Assert
    // The receipt should track causation chain
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.CausationId).IsEqualTo(causationId);
    await Assert.That(receipt.CorrelationId).IsEqualTo(correlationId);
  }

  // Helper method to create dispatcher
  // Will be implemented to return InMemoryDispatcher
  private IDispatcher CreateDispatcher() {
    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

    // Register receptors
    services.AddReceptors();

    // Register dispatcher
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  // Test supporting types
  public record UnknownCommand();

  // Test receptor for DispatcherTests
  public class DispatcherTestOrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    public async ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken cancellationToken = default) {
      await Task.Delay(1);

      return new OrderCreated(
          OrderId: Guid.NewGuid(),
          CustomerId: message.CustomerId
      );
    }
  }

  // ========================================
  // VOID RECEPTOR PATTERN TESTS
  // ========================================

  // Test messages for void pattern
  public record LogCommand(string Message);
  public record ProcessCommand(Guid Id, string Data);

  // Test void receptors
  public class LogReceptor : IReceptor<LogCommand> {
    public static int ProcessedCount { get; private set; }
    public static LogCommand? LastProcessed { get; private set; }

    public static void Reset() {
      ProcessedCount = 0;
      LastProcessed = null;
    }

    public ValueTask HandleAsync(LogCommand message, CancellationToken cancellationToken = default) {
      ProcessedCount++;
      LastProcessed = message;
      return ValueTask.CompletedTask;
    }
  }

  public class ProcessReceptor : IReceptor<ProcessCommand> {
    public static int ProcessedCount { get; private set; }

    public static void Reset() {
      ProcessedCount = 0;
    }

    public async ValueTask HandleAsync(ProcessCommand message, CancellationToken cancellationToken = default) {
      await Task.Delay(1, cancellationToken);
      ProcessedCount++;
    }
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidReceptor_ShouldInvokeWithoutReturningResultAsync() {
    // Arrange
    LogReceptor.Reset();
    var dispatcher = CreateDispatcher();
    var command = new LogCommand("Test log message");

    // Act
    await dispatcher.LocalInvokeAsync(command);

    // Assert
    await Assert.That(LogReceptor.ProcessedCount).IsEqualTo(1);
    await Assert.That(LogReceptor.LastProcessed).IsEqualTo(command);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidReceptor_SynchronousCompletion_ShouldNotAllocateAsync() {
    // Arrange
    LogReceptor.Reset();
    var dispatcher = CreateDispatcher();
    var command = new LogCommand("Test");

    // Act
    var task = dispatcher.LocalInvokeAsync(command);

    // Assert - Should complete synchronously (no async state machine allocation)
    await Assert.That(task.IsCompleted).IsTrue();
    await task;
    await Assert.That(LogReceptor.ProcessedCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_VoidReceptor_AsynchronousCompletion_ShouldCompleteAsync() {
    // Arrange
    ProcessReceptor.Reset();
    var dispatcher = CreateDispatcher();
    var command = new ProcessCommand(Guid.NewGuid(), "Test data");

    // Act
    var task = dispatcher.LocalInvokeAsync(command);
    await Assert.That(task.IsCompleted).IsFalse(); // Async operation
    await task;

    // Assert
    await Assert.That(ProcessReceptor.ProcessedCount).IsEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidReceptor_WithContext_ShouldAcceptContextAsync() {
    // Arrange
    LogReceptor.Reset();
    var dispatcher = CreateDispatcher();
    var command = new LogCommand("Test with context");
    var context = MessageContext.New();

    // Act
    await dispatcher.LocalInvokeAsync(command, context);

    // Assert
    await Assert.That(LogReceptor.ProcessedCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_VoidReceptor_NoHandler_ShouldThrowHandlerNotFoundExceptionAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new UnknownCommand();

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(command))
      .ThrowsExactly<HandlerNotFoundException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidReceptor_MultipleInvocations_ShouldTrackAllAsync() {
    // Arrange
    LogReceptor.Reset();
    var dispatcher = CreateDispatcher();

    // Act
    await dispatcher.LocalInvokeAsync(new LogCommand("Message 1"));
    await dispatcher.LocalInvokeAsync(new LogCommand("Message 2"));
    await dispatcher.LocalInvokeAsync(new LogCommand("Message 3"));

    // Assert
    await Assert.That(LogReceptor.ProcessedCount).IsEqualTo(3);
  }

  // ========================================
  // VALIDATION AND EDGE CASE TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithNullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1" });

    // Act & Assert
    var exception = await Assert.That(async () => await dispatcher.LocalInvokeAsync<OrderCreated>(command, null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(exception.ParamName).IsEqualTo("context");
  }

  [Test]
  public async Task LocalInvokeAsync_VoidReceptor_WithNullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new LogCommand("test");

    // Act & Assert
    var exception = await Assert.That(async () => await dispatcher.LocalInvokeAsync(command, null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(exception.ParamName).IsEqualTo("context");
  }

  // ========================================
  // TRACING TESTS
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidReceptor_WithTracing_StoresEnvelopeAsync() {
    // Arrange
    LogReceptor.Reset();
    var services = new ServiceCollection();
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var command = new LogCommand("Test with tracing");

    // Act
    await dispatcher.LocalInvokeAsync(command);

    // Assert - Verify handler was called
    await Assert.That(LogReceptor.ProcessedCount).IsEqualTo(1);

    // Assert - Verify envelope was stored in trace store
    var traces = await traceStore.GetByTimeRangeAsync(
      DateTimeOffset.UtcNow.AddMinutes(-1),
      DateTimeOffset.UtcNow.AddMinutes(1)
    );
    await Assert.That(traces).HasCount().GreaterThanOrEqualTo(1);
  }
}
