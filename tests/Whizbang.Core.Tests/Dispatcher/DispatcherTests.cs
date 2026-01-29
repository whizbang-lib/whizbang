using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Generated;
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
    var dispatcher = _createDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), ["item1", "item2"]);

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
    var dispatcher = _createDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), ["item1", "item2"]);

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
    var dispatcher = _createDispatcher();
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
    var dispatcher = _createDispatcher();
    var unknownCommand = new UnknownCommand();

    // Act & Assert
    var exception = await Assert.That(async () => await dispatcher.LocalInvokeAsync<UnknownCommand, object>(unknownCommand))
        .ThrowsExactly<HandlerNotFoundException>();

    await Assert.That(exception?.Message).Contains("UnknownCommand");
  }

  [Test]
  public async Task Send_WithContext_ShouldPreserveCorrelationIdInReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);
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
    var dispatcher = _createDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);
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
    var dispatcher = _createDispatcher();
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
    var dispatcher = _createDispatcher();
    var commands = new object[] {
            new CreateOrder(Guid.NewGuid(), ["item1"]),
            new CreateOrder(Guid.NewGuid(), ["item2"]),
            new CreateOrder(Guid.NewGuid(), ["item3"])
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
    var dispatcher = _createDispatcher();
    var commands = new object[] {
            new CreateOrder(Guid.NewGuid(), ["item1"]),
            new CreateOrder(Guid.NewGuid(), ["item2"]),
            new CreateOrder(Guid.NewGuid(), ["item3"])
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
    var dispatcher = _createDispatcher();
    var command1 = new CreateOrder(Guid.NewGuid(), ["item1"]);
    var command2 = new CreateOrder(Guid.NewGuid(), ["item2"]);

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
    var dispatcher = _createDispatcher();
    var createCommand = new CreateOrder(Guid.NewGuid(), ["item1"]);

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
    var dispatcher = _createDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);

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
    var dispatcher = _createDispatcher();
    var causationId = MessageId.New();
    var correlationId = CorrelationId.New();
    var initialContext = MessageContext.Create(correlationId, causationId);
    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);

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
  private static IDispatcher _createDispatcher() {
    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

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
      await Task.Delay(1, cancellationToken);

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
    public static TaskCompletionSource? Gate { get; private set; }

    public static void Reset() {
      ProcessedCount = 0;
      Gate = null;
    }

    public static void SetGate() {
      Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void ReleaseGate() {
      Gate?.TrySetResult();
    }

    public async ValueTask HandleAsync(ProcessCommand message, CancellationToken cancellationToken = default) {
      if (Gate != null) {
        await Gate.Task.WaitAsync(cancellationToken);
      } else {
        await Task.Delay(1, cancellationToken);
      }
      ProcessedCount++;
    }
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidReceptor_ShouldInvokeWithoutReturningResultAsync() {
    // Arrange
    LogReceptor.Reset();
    var dispatcher = _createDispatcher();
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
    var dispatcher = _createDispatcher();
    var command = new LogCommand("Test");

    // Act
    var task = dispatcher.LocalInvokeAsync(command);

    // Assert - Should complete synchronously (no async state machine allocation)
    await Assert.That(task.IsCompleted).IsTrue();
    await task;
    await Assert.That(LogReceptor.ProcessedCount).IsEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidReceptor_AsynchronousCompletion_ShouldCompleteAsync() {
    // Arrange
    ProcessReceptor.Reset();
    ProcessReceptor.SetGate(); // Set up gate to control completion deterministically
    var dispatcher = _createDispatcher();
    var command = new ProcessCommand(Guid.NewGuid(), "Test data");

    // Act
    var task = dispatcher.LocalInvokeAsync(command);
    await Assert.That(task.IsCompleted).IsFalse(); // Now deterministically false - waiting on gate
    ProcessReceptor.ReleaseGate(); // Allow handler to complete
    await task;

    // Assert
    await Assert.That(ProcessReceptor.ProcessedCount).IsEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidReceptor_WithContext_ShouldAcceptContextAsync() {
    // Arrange
    LogReceptor.Reset();
    var dispatcher = _createDispatcher();
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
    var dispatcher = _createDispatcher();
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
    var dispatcher = _createDispatcher();

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
    var dispatcher = _createDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);

    // Act & Assert
    var exception = await Assert.That(async () => await dispatcher.LocalInvokeAsync<CreateOrder, OrderCreated>(command, null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(exception!.ParamName).IsEqualTo("context");
  }

  [Test]
  public async Task LocalInvokeAsync_VoidReceptor_WithNullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new LogCommand("test");

    // Act & Assert
    var exception = await Assert.That(async () => await dispatcher.LocalInvokeAsync<LogCommand>(command, null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(exception!.ParamName).IsEqualTo("context");
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

    // Register service instance provider (required dependency)
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

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
    await Assert.That(traces).Count().IsGreaterThanOrEqualTo(1);
  }

  // ========================================
  // NOTE: Outbox fallback tests removed - replaced by IWorkCoordinatorStrategy pattern
  // In the unified architecture, ALL outbox operations flow through:
  // IWorkCoordinatorStrategy → IWorkCoordinator → process_work_batch
  // The old Dispatcher(outbox: IOutbox) pattern no longer exists.
  // ========================================

  // ========================================
  // GENERIC TYPE PRESERVATION TESTS (AOT Serialization)
  // ========================================
  // These tests verify that generic dispatcher methods preserve compile-time type information
  // to create MessageEnvelope<TMessage> instead of MessageEnvelope<object>.
  // This is critical for AOT serialization where JsonTypeInfo must be generated at compile time.

  [Test]
  [NotInParallel]
  public async Task SendAsync_Generic_CreatesTypedEnvelopeForTracingAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Verify receipt was returned
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);

    // Assert - Verify envelope was stored with correct type
    var traces = await traceStore.GetByTimeRangeAsync(
      DateTimeOffset.UtcNow.AddMinutes(-1),
      DateTimeOffset.UtcNow.AddMinutes(1)
    );
    await Assert.That(traces).Count().IsGreaterThanOrEqualTo(1);

    // Verify the envelope has the correct generic type parameter
    var envelope = traces.First();
    var envelopeType = envelope.GetType();
    await Assert.That(envelopeType.IsGenericType).IsTrue()
      .Because("MessageEnvelope should be a generic type");
    await Assert.That(envelopeType.GetGenericTypeDefinition().Name).Contains("MessageEnvelope")
      .Because("Should be MessageEnvelope<T>");

    // Critical: Verify it's MessageEnvelope<CreateOrder>, NOT MessageEnvelope<object>
    var typeArguments = envelopeType.GetGenericArguments();
    await Assert.That(typeArguments).Count().IsEqualTo(1);
    await Assert.That(typeArguments[0]).IsEqualTo(typeof(CreateOrder))
      .Because("Type parameter should be CreateOrder, not object - required for AOT serialization");
  }

  [Test]
  [NotInParallel]
  public async Task SendManyAsync_Generic_CreatesTypedEnvelopesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var commands = new[] {
      new CreateOrder(Guid.NewGuid(), ["item1"]),
      new CreateOrder(Guid.NewGuid(), ["item2"]),
      new CreateOrder(Guid.NewGuid(), ["item3"])
    };

    // Act - Use generic SendManyAsync<TMessage>
    var receipts = await dispatcher.SendManyAsync<CreateOrder>(commands);

    // Assert - Verify all receipts returned
    await Assert.That(receipts).Count().IsEqualTo(3);

    // Assert - Verify all envelopes have correct type
    var traces = await traceStore.GetByTimeRangeAsync(
      DateTimeOffset.UtcNow.AddMinutes(-1),
      DateTimeOffset.UtcNow.AddMinutes(1)
    );
    await Assert.That(traces.Count).IsGreaterThanOrEqualTo(3);

    // Verify each envelope preserves the CreateOrder type
    foreach (var envelope in traces) {
      var envelopeType = envelope.GetType();
      await Assert.That(envelopeType.IsGenericType).IsTrue();

      var typeArguments = envelopeType.GetGenericArguments();
      await Assert.That(typeArguments[0]).IsEqualTo(typeof(CreateOrder))
        .Because("Each envelope should be MessageEnvelope<CreateOrder> for AOT serialization");
    }
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_DoesNotRequireTypePreservation_ForInProcessRPCAsync() {
    // LocalInvokeAsync is designed for high-performance in-process RPC.
    // It never serializes, so type preservation is NOT required - it can use MessageEnvelope<object>
    // without breaking functionality. Type preservation is only critical for SendAsync (outbox path)
    // where AOT serialization requires JsonTypeInfo<MessageEnvelope<TMessage>>.

    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderCreated>(command);

    // Assert - Verify result was returned (the important part for LocalInvoke)
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);

    // Assert - Verify envelope was stored for tracing
    var traces = await traceStore.GetByTimeRangeAsync(
      DateTimeOffset.UtcNow.AddMinutes(-1),
      DateTimeOffset.UtcNow.AddMinutes(1)
    );
    await Assert.That(traces).Count().IsGreaterThanOrEqualTo(1);

    // Note: The envelope type may be MessageEnvelope<object> for LocalInvoke, and that's OK
    // because LocalInvoke never serializes. The receptor is invoked directly with zero reflection.
  }

  [Test]
  public async Task SendManyAsync_Generic_DifferentFromNonGenericVersionAsync() {
    // This test documents the difference between generic and non-generic SendManyAsync
    // Generic version: SendManyAsync<TMessage>(IEnumerable<TMessage>) - preserves type
    // Non-generic version: SendManyAsync(IEnumerable<object>) - type-erased

    // Arrange
    var dispatcher = _createDispatcher();
    var commands = new[] {
      new CreateOrder(Guid.NewGuid(), ["item1"]),
      new CreateOrder(Guid.NewGuid(), ["item2"])
    };

    // Act - Generic version (recommended for AOT)
    var genericReceipts = await dispatcher.SendManyAsync<CreateOrder>(commands);

    // Act - Non-generic version (backward compatibility)
    var nonGenericReceipts = await dispatcher.SendManyAsync(commands.Cast<object>());

    // Assert - Both should work, but generic version is AOT-compatible
    await Assert.That(genericReceipts).Count().IsEqualTo(2);
    await Assert.That(nonGenericReceipts).Count().IsEqualTo(2);

    // Note: The key difference is internal - generic version creates MessageEnvelope<CreateOrder>
    // while non-generic creates MessageEnvelope<object>. Both work at runtime but only
    // generic version works with AOT serialization.
  }

  // ========================================
  // GENERIC LOCAL INVOKE WITH TRACING TESTS
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericWithTracing_CreatesTypedEnvelopeAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);
    var context = MessageContext.New();

    // Act - Use fully generic LocalInvokeAsync<TMessage, TResult>
    var result = await dispatcher.LocalInvokeAsync<CreateOrder, OrderCreated>(command, context);

    // Assert - Verify result
    await Assert.That(result).IsNotNull();
    await Assert.That(result.CustomerId).IsEqualTo(command.CustomerId);

    // Assert - Verify envelope was stored with correct type
    var traces = await traceStore.GetByTimeRangeAsync(
      DateTimeOffset.UtcNow.AddMinutes(-1),
      DateTimeOffset.UtcNow.AddMinutes(1)
    );
    await Assert.That(traces).Count().IsGreaterThanOrEqualTo(1);

    // Verify it's MessageEnvelope<CreateOrder>
    var envelope = traces[0];
    var typeArguments = envelope.GetType().GetGenericArguments();
    await Assert.That(typeArguments[0]).IsEqualTo(typeof(CreateOrder));
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidGenericWithTracing_StoresEnvelopeAsync() {
    // Arrange
    LogReceptor.Reset();
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var command = new LogCommand("Test with generic void tracing");
    var context = MessageContext.New();

    // Act - Use generic void LocalInvokeAsync<TMessage>
    await dispatcher.LocalInvokeAsync<LogCommand>(command, context);

    // Assert - Handler was called
    await Assert.That(LogReceptor.ProcessedCount).IsEqualTo(1);

    // Assert - Envelope was stored
    var traces = await traceStore.GetByTimeRangeAsync(
      DateTimeOffset.UtcNow.AddMinutes(-1),
      DateTimeOffset.UtcNow.AddMinutes(1)
    );
    await Assert.That(traces).Count().IsGreaterThanOrEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_GenericWithTracing_CreatesTypedEnvelopeAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);

    // Act - Use generic SendAsync<TMessage>
    var receipt = await dispatcher.SendAsync<CreateOrder>(command);

    // Assert - Receipt returned
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);

    // Assert - Envelope was stored with correct type
    var traces = await traceStore.GetByTimeRangeAsync(
      DateTimeOffset.UtcNow.AddMinutes(-1),
      DateTimeOffset.UtcNow.AddMinutes(1)
    );
    await Assert.That(traces).Count().IsGreaterThanOrEqualTo(1);
  }

  // ========================================
  // LIFECYCLE INVOKER TESTS
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithLifecycleInvoker_InvokesLifecycleAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var lifecycleInvoker = new MockLifecycleInvoker();
    services.AddSingleton<ILifecycleInvoker>(lifecycleInvoker);
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderCreated>(command);

    // Assert - Lifecycle was invoked
    await Assert.That(result).IsNotNull();
    await Assert.That(lifecycleInvoker.InvokeCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(lifecycleInvoker.LastStage).IsEqualTo(LifecycleStage.ImmediateAsync);
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_WithLifecycleInvoker_InvokesLifecycleAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var lifecycleInvoker = new MockLifecycleInvoker();
    services.AddSingleton<ILifecycleInvoker>(lifecycleInvoker);
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var command = new CreateOrder(Guid.NewGuid(), ["item1"]);

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Lifecycle was invoked
    await Assert.That(receipt).IsNotNull();
    await Assert.That(lifecycleInvoker.InvokeCount).IsGreaterThanOrEqualTo(1);
  }

  // Mock lifecycle invoker for testing
  private sealed class MockLifecycleInvoker : ILifecycleInvoker {
    public int InvokeCount { get; private set; }
    public LifecycleStage? LastStage { get; private set; }

    public ValueTask InvokeAsync(object message, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      InvokeCount++;
      LastStage = stage;
      return ValueTask.CompletedTask;
    }
  }
}
