using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for Dispatcher outbox and routing functionality.
/// These tests exercise the outbox paths that require IWorkCoordinatorStrategy.
/// </summary>
public class DispatcherOutboxTests {
  // Test messages for routing (with StreamKey attributes to satisfy generator)
  public record ProductCreatedEvent([property: StreamKey] Guid ProductId) : IEvent;
  public record InventoryUpdatedEvent([property: StreamKey] Guid ProductId, int Quantity) : IEvent;
  public record OrderPlacedEvent([property: StreamKey] Guid OrderId) : IEvent;
  public record CustomEvent([property: StreamKey] string Data) : IEvent;

  // Test commands for routing
  public record CreateProductCommand(string Name);
  public record UpdateInventoryCommand(Guid ProductId, int Delta);
  public record PlaceOrderCommand(Guid CustomerId);
  public record CustomCommand(string Data);

  // Stub work coordinator strategy for testing
  private sealed class StubWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];
    public List<InboxMessage> QueuedInboxMessages { get; } = [];
    public List<(Guid messageId, MessageProcessingStatus status)> QueuedCompletions { get; } = [];
    public List<(Guid messageId, MessageProcessingStatus status, string error)> QueuedFailures { get; } = [];
    public int FlushCount { get; private set; }

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) => QueuedInboxMessages.Add(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) =>
      QueuedCompletions.Add((messageId, completedStatus));
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) =>
      QueuedCompletions.Add((messageId, completedStatus));
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) =>
      QueuedFailures.Add((messageId, completedStatus, errorMessage));
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) =>
      QueuedFailures.Add((messageId, completedStatus, errorMessage));

    public Task<WorkBatch> FlushAsync(WorkBatchFlags flags, CancellationToken ct = default) {
      FlushCount++;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  // Stub topic registry for testing
  private sealed class StubTopicRegistry : ITopicRegistry {
    private readonly Dictionary<Type, string> _topics = new();

    public void RegisterTopic<T>(string topic) => _topics[typeof(T)] = topic;
    public string? GetBaseTopic(Type messageType) => _topics.TryGetValue(messageType, out var topic) ? topic : null;
  }

  // Stub envelope serializer for testing (avoids JSON serialization complexity)
  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      // Create a minimal JsonElement payload for testing
      var jsonElement = System.Text.Json.JsonSerializer.SerializeToElement(new { });
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = jsonElement,
        Hops = []
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!
      );
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) {
      throw new NotImplementedException("Not needed for outbox routing tests");
    }
  }

  // ========================================
  // TOPIC RESOLUTION TESTS - Events
  // ========================================

  [Test]
  public async Task PublishAsync_ProductEvent_RoutesToProductsTopicAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new ProductCreatedEvent(Guid.NewGuid());

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Should route to "products" topic via convention
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("products");
  }

  [Test]
  public async Task PublishAsync_InventoryEvent_RoutesToInventoryTopicAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new InventoryUpdatedEvent(Guid.NewGuid(), 10);

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("inventory");
  }

  [Test]
  public async Task PublishAsync_OrderEvent_RoutesToOrdersTopicAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new OrderPlacedEvent(Guid.NewGuid());

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("orders");
  }

  [Test]
  public async Task PublishAsync_CustomEvent_UsesConventionFallbackAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new CustomEvent("test");

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Convention: CustomEvent -> "custom" (lowercase, without "Event" suffix)
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("custom");
  }

  [Test]
  public async Task PublishAsync_WithTopicRegistry_UsesRegisteredTopicAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var registry = new StubTopicRegistry();
    registry.RegisterTopic<CustomEvent>("my-custom-topic");
    var dispatcher = _createDispatcherWithStrategy(strategy, registry);
    var @event = new CustomEvent("test");

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Should use registered topic instead of convention
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("my-custom-topic");
  }

  // ========================================
  // OUTBOX SENDING TESTS - Commands
  // ========================================

  [Test]
  public async Task SendAsync_NoLocalHandler_RoutesToOutboxAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new CreateProductCommand("Test Product");

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Should route to outbox with "products" destination
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("products");
  }

  [Test]
  public async Task SendAsync_InventoryCommand_RoutesToInventoryDestinationAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new UpdateInventoryCommand(Guid.NewGuid(), 5);

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Convention: UpdateInventoryCommand -> "updateinventory" (lowercase, without "Command" suffix)
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("updateinventory");
  }

  [Test]
  public async Task SendAsync_OrderCommand_RoutesToOrdersDestinationAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new PlaceOrderCommand(Guid.NewGuid());

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Convention: PlaceOrderCommand -> "placeorder" (lowercase, without "Command" suffix)
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("placeorder");
  }

  [Test]
  public async Task SendAsync_CustomCommand_UsesConventionFallbackAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new CustomCommand("test");

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Convention: CustomCommand -> "custom" (lowercase, without "Command" suffix)
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("custom");
  }

  [Test]
  public async Task SendAsync_WithTopicRegistry_UsesRegisteredDestinationAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var registry = new StubTopicRegistry();
    registry.RegisterTopic<CustomCommand>("my-custom-queue");
    var dispatcher = _createDispatcherWithStrategy(strategy, registry);
    var command = new CustomCommand("test");

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("my-custom-queue");
  }

  // ========================================
  // BATCH OUTBOX TESTS
  // ========================================

  [Test]
  public async Task SendManyAsync_QueuesAllMessagesBeforeFlushAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var commands = new object[] {
      new CreateProductCommand("Product 1"),
      new CreateProductCommand("Product 2"),
      new CreateProductCommand("Product 3")
    };

    // Act
    var receipts = await dispatcher.SendManyAsync(commands);

    // Assert - All messages queued
    await Assert.That(receipts.Count()).IsEqualTo(3);
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(3);
    // Only one flush for the batch
    await Assert.That(strategy.FlushCount).IsEqualTo(1);
  }

  [Test]
  public async Task SendManyAsync_Generic_QueuesAllTypedMessagesAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var commands = new[] {
      new CreateProductCommand("Product 1"),
      new CreateProductCommand("Product 2")
    };

    // Act
    var receipts = await dispatcher.SendManyAsync<CreateProductCommand>(commands);

    // Assert
    await Assert.That(receipts.Count()).IsEqualTo(2);
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(2);
    await Assert.That(strategy.FlushCount).IsEqualTo(1);
  }

  // ========================================
  // NULL EVENT VALIDATION
  // ========================================

  [Test]
  public async Task PublishAsync_NullEvent_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync<CustomEvent>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static IDispatcher _createDispatcherWithStrategy(
    IWorkCoordinatorStrategy strategy,
    ITopicRegistry? registry = null
  ) {
    var services = new ServiceCollection();

    // Register required dependencies
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // Register stub envelope serializer (avoids JSON configuration complexity)
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();

    // Register work coordinator strategy (scoped to match typical usage)
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    // Register topic registry if provided
    if (registry != null) {
      services.AddSingleton(registry);
    }

    // Register receptors and dispatcher
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }
}
