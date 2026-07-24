using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for LocalInvokeWithReceiptAsync — In-Process RPC with Dispatch Metadata.
/// Verifies that InvokeResult combines both the business result AND delivery receipt.
/// </summary>
[Category("Dispatcher")]
[Category("InvokeWithReceipt")]
public class DispatcherInvokeWithReceiptTests {

  // ========================================
  // Test Messages and Receptors
  // ========================================

  /// <summary>Command with [StreamId] attribute</summary>
  public record CreateOrderCommand([property: StreamId] Guid OrderId, string Description) : ICommand;

  /// <summary>Response for CreateOrderCommand</summary>
  public record CreateOrderResponse(Guid OrderId);

  /// <summary>Command without [StreamId] attribute</summary>
  public record ProcessPaymentCommand(decimal Amount) : ICommand;

  /// <summary>Response for ProcessPaymentCommand</summary>
  public record ProcessPaymentResponse(bool Success);

  /// <summary>Receptor for CreateOrderCommand</summary>
  public class CreateOrderReceptor : IReceptor<CreateOrderCommand, CreateOrderResponse> {
    public ValueTask<CreateOrderResponse> HandleAsync(CreateOrderCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new CreateOrderResponse(message.OrderId));
    }
  }

  /// <summary>Receptor for ProcessPaymentCommand</summary>
  public class ProcessPaymentReceptor : IReceptor<ProcessPaymentCommand, ProcessPaymentResponse> {
    public ValueTask<ProcessPaymentResponse> HandleAsync(ProcessPaymentCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new ProcessPaymentResponse(true));
    }
  }

  // ========================================
  // Generic overload: LocalInvokeWithReceiptAsync<TMessage, TResult>
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceipt_Generic_ReturnsBusinessResultAndReceiptAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var command = new CreateOrderCommand(orderId, "Test Order");
    var dispatcher = _createDispatcher();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderCommand, CreateOrderResponse>(command);

    // Assert - Business result
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Value.OrderId).IsEqualTo(orderId);

    // Assert - Delivery receipt
    await Assert.That(invokeResult.Receipt).IsNotNull();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(invokeResult.Receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(invokeResult.Receipt.Destination).Contains("CreateOrderCommand");
  }

  // ========================================
  // Non-generic overload: LocalInvokeWithReceiptAsync<TResult>(object)
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceipt_ReturnsBusinessResultAndReceiptAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var command = new CreateOrderCommand(orderId, "Test Order");
    var dispatcher = _createDispatcher();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderResponse>((object)command);

    // Assert - Business result
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Value.OrderId).IsEqualTo(orderId);

    // Assert - Delivery receipt
    await Assert.That(invokeResult.Receipt).IsNotNull();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(invokeResult.Receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // StreamId in receipt
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceipt_StreamId_PopulatedFromStreamIdAttributeAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var command = new CreateOrderCommand(orderId, "Test Order");
    var dispatcher = _createDispatcher();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderResponse>((object)command);

    // Assert - StreamId populated from [StreamId] attribute
    await Assert.That(invokeResult.Receipt.StreamId).IsNotNull();
    await Assert.That(invokeResult.Receipt.StreamId!.Value).IsEqualTo(orderId);
  }

  [Test]
  public async Task LocalInvokeWithReceipt_NoStreamId_ReceiptStreamIdIsNullAsync() {
    // Arrange - ProcessPaymentCommand has no [StreamId] attribute
    var command = new ProcessPaymentCommand(99.99m);
    var dispatcher = _createDispatcher();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<ProcessPaymentResponse>((object)command);

    // Assert - StreamId is null when no [StreamId] attribute
    await Assert.That(invokeResult.Receipt.StreamId).IsNull();
    await Assert.That(invokeResult.Value.Success).IsTrue();
  }

  // ========================================
  // Context overload: with IMessageContext
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceipt_WithContext_PreservesCorrelationIdAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var command = new CreateOrderCommand(orderId, "Test Order");
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var context = MessageContext.Create(correlationId, causationId);
    var dispatcher = _createDispatcher();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderCommand, CreateOrderResponse>(
      command, context);

    // Assert - Context preserved in receipt
    await Assert.That(invokeResult.Receipt.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(invokeResult.Receipt.CausationId).IsEqualTo(causationId);
    await Assert.That(invokeResult.Value.OrderId).IsEqualTo(orderId);
  }

  [Test]
  public async Task LocalInvokeWithReceipt_WithContext_NonGeneric_PreservesCorrelationIdAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var command = new CreateOrderCommand(orderId, "Test Order");
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var context = MessageContext.Create(correlationId, causationId);
    var dispatcher = _createDispatcher();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderResponse>(
      (object)command, context);

    // Assert - Context preserved in receipt
    await Assert.That(invokeResult.Receipt.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(invokeResult.Receipt.CausationId).IsEqualTo(causationId);
    await Assert.That(invokeResult.Value.OrderId).IsEqualTo(orderId);
  }

  // ========================================
  // MessageId uniqueness
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceipt_MessageId_IsUniquePerInvocationAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command1 = new CreateOrderCommand(Guid.NewGuid(), "Order 1");
    var command2 = new CreateOrderCommand(Guid.NewGuid(), "Order 2");

    // Act
    var result1 = await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderResponse>((object)command1);
    var result2 = await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderResponse>((object)command2);

    // Assert - Each invocation gets a unique MessageId
    await Assert.That(result1.Receipt.MessageId).IsNotEqualTo(result2.Receipt.MessageId);
    await Assert.That(result1.Receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(result2.Receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // Error handling: ReceptorNotFoundException
  // ========================================

  public record UnknownCommand();

  [Test]
  public async Task LocalInvokeWithReceipt_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var unknownCommand = new UnknownCommand();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<object>((object)unknownCommand))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeWithReceipt_Generic_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var unknownCommand = new UnknownCommand();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<UnknownCommand, object>(unknownCommand))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // DispatchOptions overload
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceipt_WithDispatchOptions_ReturnsReceiptAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var command = new CreateOrderCommand(orderId, "Test Order");
    var options = new DispatchOptions();
    var dispatcher = _createDispatcher();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderResponse>((object)command, options);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Value.OrderId).IsEqualTo(orderId);
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(invokeResult.Receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(invokeResult.Receipt.StreamId).IsNotNull();
    await Assert.That(invokeResult.Receipt.StreamId!.Value).IsEqualTo(orderId);
  }

  [Test]
  public async Task LocalInvokeWithReceipt_WithCancelledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new CreateOrderCommand(Guid.NewGuid(), "Test Order");
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var options = new DispatchOptions().WithCancellationToken(cts.Token);

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderResponse>((object)command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task LocalInvokeWithReceipt_WithOptions_UnknownMessage_ThrowsReceptorNotFoundExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var unknownCommand = new UnknownCommand();
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<object>((object)unknownCommand, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // Without StreamIdExtractor registered
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceipt_NoExplicitExtractor_StillExtractsStreamIdViaAssemblyRegistryAsync() {
    // Arrange — no explicit IStreamIdExtractor registered, but AssemblyRegistry provides generated extractors
    var orderId = Guid.NewGuid();
    var command = new CreateOrderCommand(orderId, "Test Order");
    var dispatcher = _createDispatcherWithoutExtractor();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<CreateOrderResponse>((object)command);

    // Assert - StreamId is still populated because AssemblyRegistry contributes generated extractors
    await Assert.That(invokeResult.Receipt.StreamId).IsNotNull();
    await Assert.That(invokeResult.Receipt.StreamId!.Value).IsEqualTo(orderId);
    await Assert.That(invokeResult.Value.OrderId).IsEqualTo(orderId);
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // InvokeResult record equality
  // ========================================

  [Test]
  public async Task InvokeResult_Record_HasValueAndReceiptPropertiesAsync() {
    // Arrange
    var receipt = DeliveryReceipt.Delivered(MessageId.New(), "TestDest");
    var invokeResult = new InvokeResult<string>("hello", receipt);

    // Assert
    await Assert.That(invokeResult.Value).IsEqualTo("hello");
    await Assert.That(invokeResult.Receipt).IsEqualTo(receipt);
    await Assert.That(invokeResult.Receipt.Destination).IsEqualTo("TestDest");
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    services.AddSingleton<IReceptor<CreateOrderCommand, CreateOrderResponse>, CreateOrderReceptor>();
    services.AddSingleton<IReceptor<ProcessPaymentCommand, ProcessPaymentResponse>, ProcessPaymentReceptor>();

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    services.AddSingleton<IStreamIdExtractor, TestStreamIdExtractor>();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  private static IDispatcher _createDispatcherWithoutExtractor() {
    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    services.AddSingleton<IReceptor<CreateOrderCommand, CreateOrderResponse>, CreateOrderReceptor>();
    services.AddSingleton<IReceptor<ProcessPaymentCommand, ProcessPaymentResponse>, ProcessPaymentReceptor>();

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  // ========================================
  // Test Support Classes
  // ========================================

  private sealed class TestStreamIdExtractor : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) {
      if (message is null) {
        return null;
      }

      if (message is IEvent @event) {
        var streamId = StreamIdExtractors.TryResolveAsGuid(@event);
        if (streamId.HasValue) {
          return streamId.Value;
        }
      }

      if (message is ICommand command) {
        var streamId = StreamIdExtractors.TryResolveAsGuid(command);
        if (streamId.HasValue) {
          return streamId.Value;
        }
      }

      var result = StreamIdExtractors.TryResolveAsGuid(message);
      return result;
    }
  }
}
