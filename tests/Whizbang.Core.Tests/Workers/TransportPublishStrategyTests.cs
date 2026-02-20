using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
  public record TestMessage([StreamKey] string Id = "test-msg") : IEvent { }

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
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
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
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
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
      Flags = WorkBatchFlags.None
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
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
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
      Flags = WorkBatchFlags.None
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
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
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
      Flags = WorkBatchFlags.None
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
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
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
      Flags = WorkBatchFlags.None
    };

    // Act
    var result = await strategy.PublishAsync(work, CancellationToken.None);

    // Assert - Transport should be called for valid destinations
    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPublishedEnvelope).IsNotNull()
      .Because("Transport should be called for messages with valid destination");
    await Assert.That(transport.LastPublishedDestination!.Address).IsEqualTo("myapp.orders.events");
  }
}
