using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

// This uses the test project's generated extractors

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for Dispatcher delivery receipt StreamId functionality.
/// Verifies that IDeliveryReceipt.StreamId is correctly populated from
/// [StreamId] (events) and [StreamId] (commands).
/// </summary>
[Category("Dispatcher")]
[Category("DeliveryReceipt")]
[Category("StreamId")]
public class DispatcherDeliveryReceiptTests {

  // ========================================
  // Test Events and Commands
  // ========================================

  /// <summary>Event with [StreamId] attribute</summary>
  public record OrderCreatedEvent([property: StreamId] Guid OrderId, string CustomerName) : IEvent;

  /// <summary>Event with [StreamId] attribute</summary>
  public record ProductCreatedEvent(
    [property: StreamId] Guid ProductStreamId,
    string Name
  ) : IEvent;

  /// <summary>Event with only [StreamId] (no [StreamId])</summary>
#pragma warning disable WHIZ009 // Intentionally missing [StreamId] for testing fallback behavior
  public record InventoryAdjustedEvent([property: StreamId] Guid ProductId, int Quantity) : IEvent;
#pragma warning restore WHIZ009

  /// <summary>Command with [StreamId] attribute</summary>
  public record CreateOrderCommand([property: StreamId] Guid OrderId, string Description) : ICommand;

  /// <summary>Response for CreateOrderCommand</summary>
  public record CreateOrderResponse(Guid OrderId);

  /// <summary>Command without [StreamId] attribute</summary>
  public record ProcessPaymentCommand(decimal Amount) : ICommand;

  /// <summary>Response for ProcessPaymentCommand</summary>
  public record ProcessPaymentResponse(bool Success);

  /// <summary>Event without [StreamId] attribute</summary>
#pragma warning disable WHIZ009 // Intentionally missing [StreamId] for testing null return behavior
  public record SystemNotificationEvent(string Message) : IEvent;
#pragma warning restore WHIZ009

  // ========================================
  // Test Receptors
  // ========================================

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
  // ICommand Tests (SendAsync returns IDeliveryReceipt)
  // ========================================

  [Test]
  public async Task SendAsync_CommandWithStreamId_DeliveryReceiptHasStreamIdAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var command = new CreateOrderCommand(orderId, "Test Order");

    var dispatcher = _createDispatcher();

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - After Dispatcher is updated to use IStreamIdExtractor,
    // this should return the StreamId from [StreamId]
    await Assert.That(receipt.StreamId).IsNotNull();
    await Assert.That(receipt.StreamId!.Value).IsEqualTo(orderId);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_CommandWithoutStreamId_StreamIdIsNullAsync() {
    // Arrange
    var command = new ProcessPaymentCommand(100.00m);

    var dispatcher = _createDispatcher();

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - No [StreamId], so StreamId should be null
    await Assert.That(receipt.StreamId).IsNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_WithGeneratedExtractor_StreamIdIsExtractedAsync() {
    // Arrange - Generated extractor is automatically registered by AddWhizbangDispatcher
    var orderId = Guid.NewGuid();
    var command = new CreateOrderCommand(orderId, "Test");
    var dispatcher = _createDispatcherWithoutExtractor();

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - StreamId should be extracted by the generated extractor
    // Note: AddWhizbangDispatcher() automatically registers the generated IStreamIdExtractor
    await Assert.That(receipt.StreamId).IsNotNull();
    await Assert.That(receipt.StreamId!.Value).IsEqualTo(orderId);
  }

  // ========================================
  // IEvent Tests (PublishAsync returns IDeliveryReceipt)
  // These tests verify StreamId is correctly extracted from [StreamId] attribute.
  // ========================================

  [Test]
  public async Task PublishAsync_EventWithStreamId_DeliveryReceiptHasStreamIdAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var @event = new OrderCreatedEvent(orderId, "Test Customer");

    var dispatcher = _createDispatcher();

    // Act
    var receipt = await dispatcher.PublishAsync(@event);

    // Assert - StreamId should be extracted from [StreamId] attribute
    await Assert.That(receipt.StreamId).IsNotNull();
    await Assert.That(receipt.StreamId!.Value).IsEqualTo(orderId);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task PublishAsync_EventWithStreamId_ExtractsStreamIdAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var @event = new ProductCreatedEvent(expectedId, "Test Product");

    var dispatcher = _createDispatcher();

    // Act
    var receipt = await dispatcher.PublishAsync(@event);

    // Assert
    await Assert.That(receipt.StreamId).IsNotNull();
    await Assert.That(receipt.StreamId!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task PublishAsync_EventWithoutStreamId_StreamIdIsNullAsync() {
    // Arrange
    var @event = new SystemNotificationEvent("Test notification");

    var dispatcher = _createDispatcher();

    // Act
    var receipt = await dispatcher.PublishAsync(@event);

    // Assert - No [StreamId], so StreamId should be null
    await Assert.That(receipt.StreamId).IsNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // StreamIdExtractor Unit Tests
  // ========================================

  [Test]
  public async Task StreamIdExtractor_EventWithStreamId_ReturnsStreamIdValueAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var @event = new OrderCreatedEvent(orderId, "Test Customer");
    var extractor = new TestStreamIdExtractor();

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(orderId);
  }

  [Test]
  public async Task StreamIdExtractor_EventWithStreamId_ExtractsStreamIdAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var @event = new ProductCreatedEvent(expectedId, "Test Product");
    var extractor = new TestStreamIdExtractor();

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task StreamIdExtractor_MessageWithNoAttributes_ReturnsNullAsync() {
    // Arrange
    var @event = new SystemNotificationEvent("System message");
    var extractor = new TestStreamIdExtractor();

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert
    await Assert.That(result).IsNull();
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // Register our test receptors manually
    services.AddSingleton<IReceptor<CreateOrderCommand, CreateOrderResponse>, CreateOrderReceptor>();
    services.AddSingleton<IReceptor<ProcessPaymentCommand, ProcessPaymentResponse>, ProcessPaymentReceptor>();

    // Register receptors from generated code and dispatcher
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    // Register test-specific IStreamIdExtractor that uses the test project's generated extractors
    services.AddSingleton<IStreamIdExtractor, TestStreamIdExtractor>();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  private static IDispatcher _createDispatcherWithoutExtractor() {
    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // Register our test receptors manually
    services.AddSingleton<IReceptor<CreateOrderCommand, CreateOrderResponse>, CreateOrderReceptor>();
    services.AddSingleton<IReceptor<ProcessPaymentCommand, ProcessPaymentResponse>, ProcessPaymentReceptor>();

    // Register receptors and dispatcher - but NO IStreamIdExtractor
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  // ========================================
  // Test Support Classes
  // ========================================

  /// <summary>
  /// Test-specific StreamIdExtractor that uses the test project's generated extractors.
  /// This is needed because the test project generates its own StreamIdExtractors
  /// in Whizbang.Core.Tests.Generated, separate from Whizbang.Core.Generated.
  /// </summary>
  private sealed class TestStreamIdExtractor : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) {
      if (message is null) {
        return null;
      }

      // For IEvent: Try [StreamId] using the test project's generated extractors
      if (message is IEvent @event) {
        var streamId = StreamIdExtractors.TryResolveAsGuid(@event);
        if (streamId.HasValue) {
          return streamId.Value;
        }
      }

      // For ICommand: Try [StreamId] using the test project's generated extractors
      if (message is ICommand command) {
        var streamId = StreamIdExtractors.TryResolveAsGuid(command);
        if (streamId.HasValue) {
          return streamId.Value;
        }
      }

      // Fallback: Try the generic overload for any message type
      var result = StreamIdExtractors.TryResolveAsGuid(message);
      return result;
    }
  }
}
