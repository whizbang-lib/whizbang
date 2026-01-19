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
  // ROUTING STRATEGY TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithRoutingStrategy_TransformsDestinationAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new PoolSuffixRoutingStrategy("-pool1");
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy: routingStrategy);
    var command = new CreateProductCommand("Test Product");

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Routing strategy should add pool suffix to "products" -> "products-pool1"
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("products-pool1");
  }

  [Test]
  public async Task PublishAsync_WithRoutingStrategy_TransformsTopicAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new PoolSuffixRoutingStrategy("-pool2");
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy: routingStrategy);
    var @event = new ProductCreatedEvent(Guid.NewGuid());

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Routing strategy should add pool suffix to "products" -> "products-pool2"
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("products-pool2");
  }

  // ========================================
  // AGGREGATE ID EXTRACTION TESTS
  // ========================================

  // Test message with AggregateId attribute
  public record OrderCommandWithAggregateId([property: AggregateId] Guid OrderId, string Description);

  [Test]
  public async Task SendAsync_WithAggregateId_ExtractsStreamIdAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var aggregateIdExtractor = new StubAggregateIdExtractor();
    var dispatcher = _createDispatcherWithStrategy(strategy, aggregateIdExtractor: aggregateIdExtractor);
    var orderId = Guid.NewGuid();
    var command = new OrderCommandWithAggregateId(orderId, "Test Order");

    // Act
    await dispatcher.SendAsync(command);

    // Assert - Stream ID should be extracted aggregate ID
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].StreamId).IsEqualTo(orderId);
  }

  [Test]
  public async Task PublishAsync_WithAggregateId_ExtractsStreamIdAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var aggregateIdExtractor = new StubAggregateIdExtractor();
    var dispatcher = _createDispatcherWithStrategy(strategy, aggregateIdExtractor: aggregateIdExtractor);
    var productId = Guid.NewGuid();
    var @event = new ProductCreatedEvent(productId);

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Stream ID should be the product ID from aggregate ID attribute
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].StreamId).IsEqualTo(productId);
  }

  [Test]
  public async Task SendAsync_WithoutAggregateId_UsesMessageIdAsStreamIdAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    // No aggregate ID extractor - should fall back to message ID
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new CustomCommand("test");

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Stream ID should be the message ID (since no aggregate ID extracted)
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    var outboxMessage = strategy.QueuedOutboxMessages[0];
    await Assert.That(outboxMessage.StreamId).IsEqualTo(outboxMessage.MessageId);
  }

  // ========================================
  // ENVELOPE METADATA TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithAggregateIdExtractor_CreatesHopMetadataAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var aggregateIdExtractor = new StubAggregateIdExtractor();
    var dispatcher = _createDispatcherWithStrategy(strategy, aggregateIdExtractor: aggregateIdExtractor);
    var productId = Guid.NewGuid();
    var @event = new ProductCreatedEvent(productId);

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Envelope metadata should contain AggregateId
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    var metadata = strategy.QueuedOutboxMessages[0].Metadata;
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Hops).Count().IsGreaterThanOrEqualTo(1);
  }

  // ========================================
  // FLUSH BEHAVIOR TESTS
  // ========================================

  [Test]
  public async Task SendAsync_CallsFlushOnStrategyAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new CreateProductCommand("Test");

    // Act
    await dispatcher.SendAsync(command);

    // Assert - Flush should be called
    await Assert.That(strategy.FlushCount).IsEqualTo(1);
  }

  [Test]
  public async Task PublishAsync_CallsFlushOnStrategyAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new ProductCreatedEvent(Guid.NewGuid());

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Flush should be called
    await Assert.That(strategy.FlushCount).IsEqualTo(1);
  }

  // ========================================
  // OUTBOX MESSAGE PROPERTIES TESTS
  // ========================================

  [Test]
  public async Task PublishAsync_SetsIsEventTrueAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new ProductCreatedEvent(Guid.NewGuid());

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - IsEvent should be true for events
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].IsEvent).IsTrue();
  }

  [Test]
  public async Task SendAsync_SetsIsEventFalseAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new CreateProductCommand("Test");

    // Act
    await dispatcher.SendAsync(command);

    // Assert - IsEvent should be false for commands
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].IsEvent).IsFalse();
  }

  [Test]
  public async Task SendAsync_SetsMessageTypeAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new CreateProductCommand("Test");

    // Act
    await dispatcher.SendAsync(command);

    // Assert - MessageType should contain the command type name
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    var messageType = strategy.QueuedOutboxMessages[0].MessageType;
    await Assert.That(messageType).Contains("CreateProductCommand");
  }

  [Test]
  public async Task PublishAsync_SetsEnvelopeTypeAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new ProductCreatedEvent(Guid.NewGuid());

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - EnvelopeType should be set
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    var envelopeType = strategy.QueuedOutboxMessages[0].EnvelopeType;
    await Assert.That(envelopeType).Contains("MessageEnvelope");
  }

  // ========================================
  // DELIVERY RECEIPT TESTS
  // ========================================

  [Test]
  public async Task SendAsync_ReturnsAcceptedReceiptAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new CreateProductCommand("Test");

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(receipt.Destination).IsEqualTo("products");
  }

  [Test]
  public async Task SendAsync_ReturnsMessageIdInReceiptAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var command = new CreateProductCommand("Test");

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Receipt's message ID should match the queued message
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(receipt.MessageId).IsEqualTo(MessageId.From(strategy.QueuedOutboxMessages[0].MessageId));
  }

  // ========================================
  // STUB IMPLEMENTATIONS
  // ========================================

  // Stub routing strategy for testing
  private sealed class PoolSuffixRoutingStrategy(string suffix) : ITopicRoutingStrategy {
    public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
      return baseTopic + suffix;
    }
  }

  // Stub aggregate ID extractor for testing
  private sealed class StubAggregateIdExtractor : IAggregateIdExtractor {
    public Guid? ExtractAggregateId(object message, Type messageType) {
      // Check for ProductCreatedEvent
      if (message is ProductCreatedEvent pce) {
        return pce.ProductId;
      }
      // Check for OrderCommandWithAggregateId
      if (message is OrderCommandWithAggregateId oca) {
        return oca.OrderId;
      }
      return null;
    }
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static IDispatcher _createDispatcherWithStrategy(
    IWorkCoordinatorStrategy strategy,
    ITopicRegistry? registry = null,
    ITopicRoutingStrategy? routingStrategy = null,
    IAggregateIdExtractor? aggregateIdExtractor = null
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

    // Register routing strategy if provided
    if (routingStrategy != null) {
      services.AddSingleton(routingStrategy);
    }

    // Register aggregate ID extractor if provided
    if (aggregateIdExtractor != null) {
      services.AddSingleton(aggregateIdExtractor);
    }

    // Register receptors and dispatcher
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }
}
