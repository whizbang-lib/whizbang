using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

public class TransportPublishStrategyTests {
  // Simple test message type for envelope creation
  public record TestMessage([StreamId] string Id = "test-msg") : IEvent { }

  // Helper to create a MessageEnvelope for testing
  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement>(
      messageId: MessageId.From(messageId),
      payload: JsonDocument.Parse("{}").RootElement,
      hops: []
    );
  }

  private sealed class TestTransport : ITransport {
    private bool _isInitialized;

    public bool IsInitialized => _isInitialized;
    public TransportCapabilities Capabilities => new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) {
      _isInitialized = true;
      return Task.CompletedTask;
    }

    public Task<Exception?> PublishResult { get; set; } = Task.FromResult<Exception?>(null);

    public OutboxWork? LastPublishedWork { get; private set; }
    public IMessageEnvelope? LastPublishedEnvelope { get; private set; }
    public TransportDestination? LastPublishedDestination { get; private set; }

    public async Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, CancellationToken cancellationToken = default) {
      LastPublishedEnvelope = envelope;
      LastPublishedDestination = destination;

      var exception = await PublishResult;
      if (exception != null) {
        throw exception;
      }
    }

    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull {
      throw new NotImplementedException();
    }
  }

  private sealed class BulkPublishCapableTestTransport : ITransport {
    private bool _isInitialized;

    public bool IsInitialized => _isInitialized;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable | TransportCapabilities.BulkPublish;

    public Task InitializeAsync(CancellationToken cancellationToken = default) {
      _isInitialized = true;
      return Task.CompletedTask;
    }

    public List<(IReadOnlyList<BulkPublishItem> Items, TransportDestination Destination)> PublishBatchCalls { get; } = [];
    public Func<IReadOnlyList<BulkPublishItem>, TransportDestination, Task<IReadOnlyList<BulkPublishItemResult>>>? PublishBatchHandler { get; set; }

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(IReadOnlyList<BulkPublishItem> items, TransportDestination destination, CancellationToken cancellationToken = default) {
      PublishBatchCalls.Add((items, destination));

      if (PublishBatchHandler is not null) {
        return await PublishBatchHandler(items, destination);
      }

      // Default: all succeed
      return items.Select(i => new BulkPublishItemResult { MessageId = i.MessageId, Success = true }).ToList();
    }

    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull {
      throw new NotImplementedException();
    }
  }

  [Test]
  public async Task Constructor_NullTransport_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();

    // Act & Assert
    await Assert.That(() => new TransportPublishStrategy(null!, readinessCheck))
      .Throws<ArgumentNullException>()
      .Because("Transport cannot be null");
  }

  [Test]
  public async Task Constructor_NullReadinessCheck_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var transport = new TestTransport();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Act & Assert
    await Assert.That(() => new TransportPublishStrategy(transport, null!))
      .Throws<ArgumentNullException>()
      .Because("ReadinessCheck cannot be null");
  }

  [Test]
  public async Task IsReadyAsync_DefaultReadinessCheck_ReturnsTrueAsync() {
    // Arrange
    var transport = new TestTransport();
    _ = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    // Act
    var result = await strategy.IsReadyAsync();

    // Assert
    await Assert.That(result).IsTrue()
      .Because("DefaultTransportReadinessCheck always returns true");
  }

  [Test]
  public async Task PublishAsync_SuccessfulPublish_ShouldReturnSuccessResultAsync() {
    // Arrange
    var transport = new TestTransport();
    _ = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.CompletedStatus).IsEqualTo(MessageProcessingStatus.Published);
    await Assert.That(result.Error).IsNull();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("test-topic");
  }

  [Test]
  public async Task PublishAsync_TransportFailure_ShouldReturnFailureResultAsync() {
    // Arrange
    var transport = new TestTransport {
      PublishResult = Task.FromResult<Exception?>(new InvalidOperationException("Transport unavailable"))
    };
    _ = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.CompletedStatus).IsEqualTo(MessageProcessingStatus.Stored);
    await Assert.That(result.Error).IsNotNull();
    await Assert.That(result.Error).Contains("Transport unavailable");
  }

  [Test]
  public async Task PublishAsync_WithNullScope_ShouldPublishSuccessfullyAsync() {
    // Arrange
    var transport = new TestTransport();
    _ = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedEnvelope).IsNotNull();
  }

  [Test]
  public async Task PublishAsync_WithStreamId_ShouldIncludeInEnvelopeAsync() {
    // Arrange
    var transport = new TestTransport();
    _ = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedEnvelope).IsNotNull();
    // StreamId should be used for message ordering/routing in envelope
  }

  [Test]
  public async Task PublishAsync_WithRoutingStrategy_CommandRoutedToInboxAsync() {
    // Arrange - This test verifies the CRITICAL routing behavior:
    // Commands (like CreateTenantCommand) must be routed to the shared inbox topic,
    // NOT to a topic named after the command type
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();

    // Routing is now AUTOMATIC - no explicit routing strategy needed
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    // Simulate a command being published - destination is the command type name,
    // but it should be routed to "inbox" instead
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "createtenantcommand", // This is WRONG - will be transformed
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Commands.CreateTenantCommand, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Commands.CreateTenantCommand, MyApp", // Namespace contains "Commands"
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    // CRITICAL: Commands MUST be routed to "inbox" (shared inbox), NOT "createtenantcommand"
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("inbox")
      .Because("Commands must be routed to the shared inbox topic, not individual command topics");
  }

  [Test]
  public async Task PublishAsync_WithRoutingStrategy_EventUsesDestinationDirectlyAsync() {
    // Arrange - Events should use the destination directly (already namespace topic)
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();

    // Routing is now AUTOMATIC - events detected and use destination directly
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    // Events have namespace ending in "Events"
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "myapp.orders.events", // Event namespace topic
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Orders.Events.OrderCreatedEvent, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Orders.Events.OrderCreatedEvent, MyApp", // Namespace contains "Events"
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    // Events use destination directly (already the namespace topic)
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("myapp.orders.events")
      .Because("Events should be published to their namespace topic");
  }

  [Test]
  public async Task PublishAsync_WithoutRoutingStrategy_CommandStillRoutedToInboxAsync() {
    // Arrange - Even without explicit routing, commands MUST go to inbox
    // This is critical for message delivery - commands to non-existent topics are lost!
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();

    // No routing strategy - using simple constructor
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "createtenantcommand", // This is WRONG - will be transformed to inbox
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Commands.CreateTenantCommand, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Commands.CreateTenantCommand, MyApp",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    // CRITICAL: Commands MUST be routed to inbox even without explicit routing config
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("inbox")
      .Because("Commands must ALWAYS be routed to shared inbox to ensure delivery");
  }

  [Test]
  public async Task PublishAsync_WithCustomInboxTopic_CommandRoutedToCustomTopicAsync() {
    // Arrange - This test verifies that a custom inbox topic can be configured
    // For example, JDX uses "whizbang" as their inbox exchange
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();

    // Use custom inbox topic "whizbang" instead of default "inbox"
    var strategy = new TransportPublishStrategy(transport, readinessCheck, "whizbang");

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "createtenantcommand",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Commands.CreateTenantCommand, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Commands.CreateTenantCommand, MyApp",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    // Commands should be routed to the custom "whizbang" topic
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("whizbang")
      .Because("Commands should be routed to the configured custom inbox topic");
  }

  [Test]
  public async Task PublishAsync_NestedClassCommand_RoutedToInboxAsync() {
    // Arrange - This test verifies nested class types work correctly
    // JDX uses nested types like AuthContracts+CreateTenantCommand
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "createtenant",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[JDX.Contracts.Auth.AuthContracts+CreateTenantCommand, JDX.Contracts]], Whizbang.Core",
      // Nested class uses '+' notation: Namespace.OuterClass+NestedClass
      MessageType = "JDX.Contracts.Auth.AuthContracts+CreateTenantCommand, JDX.Contracts",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    // Even though namespace doesn't contain "Commands", the type name ends with "Command"
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("inbox")
      .Because("Nested class commands ending with 'Command' must be routed to inbox");
  }

  // ========================================
  // EVENT-STORE-ONLY BYPASS TESTS
  // ========================================
  // These tests verify that messages with null/empty destination
  // (event-store-only mode) skip transport but return success.

  [Test]
  public async Task PublishAsync_WithNullDestination_SkipsTransportAndReturnsSuccessAsync() {
    // Arrange - Null destination indicates event-store-only mode
    // Event is stored in wh_event_store but should not be transported
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = null, // Event-store-only mode
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.OrderCreatedEvent, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Events.OrderCreatedEvent, MyApp",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue()
      .Because("Event-store-only messages should return success");
    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.CompletedStatus).IsEqualTo(MessageProcessingStatus.Published)
      .Because("Message should be marked as published even though transport was skipped");
    await Assert.That(result.Error).IsNull();
  }

  [Test]
  public async Task PublishAsync_WithEmptyDestination_SkipsTransportAndReturnsSuccessAsync() {
    // Arrange - Empty string destination also indicates event-store-only mode
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "", // Empty destination = event-store-only mode
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.OrderUpdatedEvent, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Events.OrderUpdatedEvent, MyApp",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue()
      .Because("Event-store-only messages with empty destination should return success");
    await Assert.That(result.CompletedStatus).IsEqualTo(MessageProcessingStatus.Published);
  }

  [Test]
  public async Task PublishAsync_WithNullDestination_TransportNotCalledAsync() {
    // Arrange - Verify transport is never invoked for event-store-only messages
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = null, // Event-store-only mode
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.OrderDeletedEvent, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Events.OrderDeletedEvent, MyApp",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    await strategy.PublishAsync(work, CancellationToken.None);

    // Assert - Transport should NOT have been called
    await Assert.That(transport.LastPublishedEnvelope).IsNull()
      .Because("Transport should not be called for event-store-only messages");
    await Assert.That(transport.LastPublishedDestination).IsNull()
      .Because("No destination should be set when transport is skipped");
  }

  [Test]
  public async Task PublishAsync_WithNullDestination_DoesNotThrowEvenIfTransportWouldFailAsync() {
    // Arrange - Even if transport would throw, event-store-only should succeed
    var transport = new TestTransport {
      PublishResult = Task.FromResult<Exception?>(new InvalidOperationException("Transport should not be called"))
    };
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = null, // Event-store-only mode
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.TestEvent, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Events.TestEvent, MyApp",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert - Should succeed without calling transport
    await Assert.That(result.Success).IsTrue()
      .Because("Event-store-only should bypass transport entirely");
    await Assert.That(transport.LastPublishedEnvelope).IsNull()
      .Because("Transport should not be called");
  }

  [Test]
  public async Task PublishAsync_WithValidDestination_StillCallsTransportAsync() {
    // Arrange - Messages with valid destination should still use transport
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "myapp.orders.events", // Valid destination = use transport
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Orders.Events.OrderShippedEvent, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Orders.Events.OrderShippedEvent, MyApp",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert - Transport should be called for valid destinations
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedEnvelope).IsNotNull()
      .Because("Transport should be called for messages with valid destination");
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("myapp.orders.events");
  }

  // ========================================
  // ROUTING KEY TESTS (Subject for ASB)
  // ========================================
  // These tests verify that RoutingKey is correctly set for Azure Service Bus
  // The RoutingKey becomes the Subject property, used for SqlFilter matching

  [Test]
  public async Task PublishAsync_Command_RoutingKeySetToNamespaceAndTypeNameAsync() {
    // Arrange - Commands must have RoutingKey set for SqlFilter matching
    // RoutingKey format: namespace.typename (lowercase)
    // This becomes the Subject property in Azure Service Bus
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "createusercommand",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Commands.CreateUserCommand, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Commands.CreateUserCommand, MyApp",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("inbox");
    // RoutingKey should be namespace.typename (lowercase) for SqlFilter matching
    // SqlFilter pattern: [Subject] LIKE 'myapp.commands.%' should match this
    await Assert.That(transport.LastPublishedDestination.RoutingKey)
      .IsEqualTo("myapp.commands.createusercommand")
      .Because("RoutingKey must be namespace.typename for SqlFilter matching");
  }

  [Test]
  public async Task PublishAsync_Event_RoutingKeySetToNamespaceAndTypeNameAsync() {
    // Arrange - Events must have RoutingKey set for SqlFilter matching
    // This is CRITICAL: Without RoutingKey, Subject defaults to "message"
    // and SqlFilter patterns like '[Subject] LIKE 'myapp.orders.%' won't match
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "myapp.orders.events", // Event namespace topic
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Orders.Events.OrderCreatedEvent, MyApp]], Whizbang.Core",
      MessageType = "MyApp.Orders.Events.OrderCreatedEvent, MyApp",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("myapp.orders.events");
    // RoutingKey must be set for events - this was the bug!
    // Without RoutingKey, Azure Service Bus sets Subject = "message"
    // and SqlFilter '[Subject] LIKE 'myapp.orders.%' won't match
    await Assert.That(transport.LastPublishedDestination.RoutingKey)
      .IsEqualTo("myapp.orders.events.ordercreatedevent")
      .Because("Events must have RoutingKey set for SqlFilter matching - Subject defaults to 'message' otherwise");
  }

  [Test]
  public async Task PublishAsync_NestedClassEvent_RoutingKeySetCorrectlyAsync() {
    // Arrange - Nested class types (OuterClass+InnerType) should work correctly
    // JDX uses patterns like JDX.Contracts.Chat.ChatConversationsContracts+CreateCommand
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "jdx.contracts.chat.events", // Event namespace topic
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[JDX.Contracts.Chat.ChatEvents+ConversationCreatedEvent, JDX.Contracts]], Whizbang.Core",
      MessageType = "JDX.Contracts.Chat.ChatEvents+ConversationCreatedEvent, JDX.Contracts",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("jdx.contracts.chat.events");
    // RoutingKey should include the full nested type name (with +)
    await Assert.That(transport.LastPublishedDestination.RoutingKey)
      .IsEqualTo("jdx.contracts.chat.chatevents+conversationcreatedevent")
      .Because("Nested class event types should have RoutingKey with full type name including +");
  }

  [Test]
  public async Task PublishAsync_NestedClassCommand_RoutingKeySetCorrectlyAsync() {
    // Arrange - Nested class commands (like ChatConversationsContracts+CreateCommand)
    // should have RoutingKey set correctly for SqlFilter matching
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "createconversation",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[JDX.Contracts.Chat.ChatConversationsContracts+CreateCommand, JDX.Contracts]], Whizbang.Core",
      MessageType = "JDX.Contracts.Chat.ChatConversationsContracts+CreateCommand, JDX.Contracts",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("inbox");
    // RoutingKey for nested class should include the + character
    // SqlFilter pattern '[Subject] LIKE 'jdx.contracts.chat.%' should match this
    await Assert.That(transport.LastPublishedDestination.RoutingKey)
      .IsEqualTo("jdx.contracts.chat.chatconversationscontracts+createcommand")
      .Because("Nested command types should have RoutingKey matching SqlFilter pattern");
  }

  [Test]
  public async Task PublishAsync_Command_RoutingKeyMatchesSqlFilterPatternAsync() {
    // Arrange - This test verifies the RoutingKey will match SqlFilter patterns
    // SqlFilter: [Subject] LIKE 'jdx.contracts.chat.%'
    // RoutingKey: jdx.contracts.chat.activitytrackedcommand
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "activitytracked",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[JDX.Contracts.Chat.ActivityTrackedCommand, JDX.Contracts]], Whizbang.Core",
      MessageType = "JDX.Contracts.Chat.ActivityTrackedCommand, JDX.Contracts",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedDestination).IsNotNull();
    var routingKey = transport.LastPublishedDestination!.RoutingKey;

    // The RoutingKey MUST start with the pattern that SqlFilter expects
    // SqlFilter: [Subject] LIKE 'jdx.contracts.chat.%'
    await Assert.That(routingKey)
      .StartsWith("jdx.contracts.chat.")
      .Because("RoutingKey must match SqlFilter pattern '[Subject] LIKE 'jdx.contracts.chat.%'");
    await Assert.That(routingKey)
      .IsEqualTo("jdx.contracts.chat.activitytrackedcommand");
  }

  // ========================================
  // BULK PUBLISH TESTS
  // ========================================

  [Test]
  public async Task SupportsBulkPublish_WithBulkCapableTransport_ReturnsTrueAsync() {
    // Arrange
    var transport = new BulkPublishCapableTestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    // Act & Assert
    await Assert.That(strategy.SupportsBulkPublish).IsTrue();
  }

  [Test]
  public async Task SupportsBulkPublish_WithoutBulkCapableTransport_ReturnsFalseAsync() {
    // Arrange
    var transport = new TestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    // Act & Assert
    await Assert.That(strategy.SupportsBulkPublish).IsFalse();
  }

  [Test]
  public async Task PublishBatchAsync_SingleDestination_CallsTransportOnceAsync() {
    // Arrange
    var transport = new BulkPublishCapableTestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var work1 = _createEventOutboxWork("myapp.orders.events", "MyApp.Orders.Events.OrderCreatedEvent, MyApp");
    var work2 = _createEventOutboxWork("myapp.orders.events", "MyApp.Orders.Events.OrderUpdatedEvent, MyApp");

    // Act
    var results = await strategy.PublishBatchAsync([work1, work2], CancellationToken.None);

    // Assert
    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results[0].Success).IsTrue();
    await Assert.That(results[1].Success).IsTrue();
    await Assert.That(transport.PublishBatchCalls).Count().IsEqualTo(1);
    await Assert.That(transport.PublishBatchCalls[0].Destination.Address).IsEqualTo("myapp.orders.events");
  }

  [Test]
  public async Task PublishBatchAsync_MultipleDestinations_GroupsByAddressAsync() {
    // Arrange
    var transport = new BulkPublishCapableTestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var eventWork1 = _createEventOutboxWork("myapp.orders.events", "MyApp.Orders.Events.OrderCreatedEvent, MyApp");
    var eventWork2 = _createEventOutboxWork("myapp.users.events", "MyApp.Users.Events.UserCreatedEvent, MyApp");
    var commandWork = _createCommandOutboxWork("MyApp.Commands.CreateTenantCommand, MyApp");

    // Act
    var results = await strategy.PublishBatchAsync([eventWork1, eventWork2, commandWork], CancellationToken.None);

    // Assert — 3 groups: myapp.orders.events, myapp.users.events, inbox
    await Assert.That(results.Count).IsEqualTo(3);
    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(transport.PublishBatchCalls).Count().IsEqualTo(3);

    var addresses = transport.PublishBatchCalls.Select(c => c.Destination.Address).OrderBy(a => a).ToList();
    await Assert.That(addresses).Contains("inbox");
    await Assert.That(addresses).Contains("myapp.orders.events");
    await Assert.That(addresses).Contains("myapp.users.events");
  }

  [Test]
  public async Task PublishBatchAsync_EventStoreOnlyItems_ReturnSuccessWithoutCallingTransportAsync() {
    // Arrange
    var transport = new BulkPublishCapableTestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var eventStoreOnly = _createEventOutboxWork(null, "MyApp.Events.LocalEvent, MyApp");
    var normalEvent = _createEventOutboxWork("myapp.orders.events", "MyApp.Orders.Events.OrderCreatedEvent, MyApp");

    // Act
    var results = await strategy.PublishBatchAsync([eventStoreOnly, normalEvent], CancellationToken.None);

    // Assert — event-store-only skipped, normal event published
    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results.All(r => r.Success)).IsTrue();
    // Only 1 transport call (for the normal event)
    await Assert.That(transport.PublishBatchCalls).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PublishBatchAsync_TransportThrowsForGroup_FailsOnlyThatGroupAsync() {
    // Arrange
    var transport = new BulkPublishCapableTestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    // Make transport throw for the orders topic
    transport.PublishBatchHandler = (items, destination) => {
      if (destination.Address == "myapp.orders.events") {
        throw new InvalidOperationException("Transport unavailable for orders");
      }
      return Task.FromResult<IReadOnlyList<BulkPublishItemResult>>(
        items.Select(i => new BulkPublishItemResult { MessageId = i.MessageId, Success = true }).ToList());
    };

    var ordersEvent = _createEventOutboxWork("myapp.orders.events", "MyApp.Orders.Events.OrderCreatedEvent, MyApp");
    var usersEvent = _createEventOutboxWork("myapp.users.events", "MyApp.Users.Events.UserCreatedEvent, MyApp");

    // Act
    var results = await strategy.PublishBatchAsync([ordersEvent, usersEvent], CancellationToken.None);

    // Assert — orders event fails, users event succeeds
    await Assert.That(results.Count).IsEqualTo(2);
    var ordersResult = results.First(r => r.MessageId == ordersEvent.MessageId);
    var usersResult = results.First(r => r.MessageId == usersEvent.MessageId);
    await Assert.That(ordersResult.Success).IsFalse();
    await Assert.That(ordersResult.Error).Contains("Transport unavailable");
    await Assert.That(usersResult.Success).IsTrue();
  }

  [Test]
  public async Task PublishBatchAsync_PerItemRoutingKeys_SetCorrectlyAsync() {
    // Arrange
    var transport = new BulkPublishCapableTestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var work1 = _createEventOutboxWork("myapp.orders.events", "MyApp.Orders.Events.OrderCreatedEvent, MyApp");
    var work2 = _createEventOutboxWork("myapp.orders.events", "MyApp.Orders.Events.OrderUpdatedEvent, MyApp");

    // Act
    await strategy.PublishBatchAsync([work1, work2], CancellationToken.None);

    // Assert — each item should have its own routing key
    await Assert.That(transport.PublishBatchCalls).Count().IsEqualTo(1);
    var items = transport.PublishBatchCalls[0].Items;
    await Assert.That(items).Count().IsEqualTo(2);
    await Assert.That(items[0].RoutingKey).IsEqualTo("myapp.orders.events.ordercreatedevent");
    await Assert.That(items[1].RoutingKey).IsEqualTo("myapp.orders.events.orderupdatedevent");
  }

  [Test]
  public async Task PublishBatchAsync_EmptyList_ReturnsEmptyResultsAsync() {
    // Arrange
    var transport = new BulkPublishCapableTestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    // Act
    var results = await strategy.PublishBatchAsync([], CancellationToken.None);

    // Assert
    await Assert.That(results.Count).IsEqualTo(0);
    await Assert.That(transport.PublishBatchCalls).Count().IsEqualTo(0);
  }

  [Test]
  public async Task PublishBatchAsync_PartialItemResults_MapsCorrectlyAsync() {
    // Arrange — transport returns mixed per-item results
    var transport = new BulkPublishCapableTestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var work1 = _createEventOutboxWork("myapp.orders.events", "MyApp.Orders.Events.OrderCreatedEvent, MyApp");
    var work2 = _createEventOutboxWork("myapp.orders.events", "MyApp.Orders.Events.OrderUpdatedEvent, MyApp");

    transport.PublishBatchHandler = (items, _) => {
      var results = new List<BulkPublishItemResult> {
        new() { MessageId = items[0].MessageId, Success = true },
        new() { MessageId = items[1].MessageId, Success = false, Error = "Message too large" }
      };
      return Task.FromResult<IReadOnlyList<BulkPublishItemResult>>(results);
    };

    // Act
    var results = await strategy.PublishBatchAsync([work1, work2], CancellationToken.None);

    // Assert
    await Assert.That(results.Count).IsEqualTo(2);
    var result1 = results.First(r => r.MessageId == work1.MessageId);
    var result2 = results.First(r => r.MessageId == work2.MessageId);
    await Assert.That(result1.Success).IsTrue();
    await Assert.That(result2.Success).IsFalse();
    await Assert.That(result2.Error).Contains("Message too large");
  }

  [Test]
  public async Task PublishBatchAsync_AllEventStoreOnly_NoTransportCallsAsync() {
    // Arrange
    var transport = new BulkPublishCapableTestTransport();
    var readinessCheck = new DefaultTransportReadinessCheck();
    var strategy = new TransportPublishStrategy(transport, readinessCheck);

    var work1 = _createEventOutboxWork(null, "MyApp.Events.Event1, MyApp");
    var work2 = _createEventOutboxWork("", "MyApp.Events.Event2, MyApp");

    // Act
    var results = await strategy.PublishBatchAsync([work1, work2], CancellationToken.None);

    // Assert
    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(results.All(r => r.CompletedStatus == MessageProcessingStatus.Published)).IsTrue();
    await Assert.That(transport.PublishBatchCalls).Count().IsEqualTo(0);
  }

  // Helper to create event OutboxWork
  private static OutboxWork _createEventOutboxWork(string? destination, string messageType) {
    var messageId = Guid.CreateVersion7();
    return new OutboxWork {
      MessageId = messageId,
      Destination = destination,
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{messageType}]], Whizbang.Core",
      MessageType = messageType,
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };
  }

  // Helper to create command OutboxWork
  private static OutboxWork _createCommandOutboxWork(string messageType) {
    var messageId = Guid.CreateVersion7();
    return new OutboxWork {
      MessageId = messageId,
      Destination = "createtenantcommand", // Will be re-routed to inbox
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{messageType}]], Whizbang.Core",
      MessageType = messageType,
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };
  }
}
