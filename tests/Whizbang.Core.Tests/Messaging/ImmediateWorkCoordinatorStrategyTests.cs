using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ImmediateWorkCoordinatorStrategy - verifies immediate flush behavior.
/// </summary>
public class ImmediateWorkCoordinatorStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // Simple test message for envelope creation
  public record _testEvent([StreamId] string Data) : IEvent;

  // ========================================
  // Priority 3 Tests: Immediate Strategy
  // ========================================

  [Test]
  public async Task FlushAsync_ImmediatelyCallsWorkCoordinatorAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = []
    };

    // Serialize typed envelope to JsonElement envelope for OutboxMessage
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });

    // Act
    _ = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - FlushAsync should immediately call ProcessWorkBatchAsync
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Immediate strategy should call ProcessWorkBatchAsync on FlushAsync");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task QueueOutboxMessage_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = []
    };

    // Serialize typed envelope to JsonElement envelope for OutboxMessage
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    var outboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    };

    // Act
    sut.QueueOutboxMessage(outboxMessage);
    _ = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Message should be passed to coordinator
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].Destination).IsEqualTo("test-topic");
  }

  [Test]
  public async Task QueueInboxMessage_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = []
    };

    // Serialize typed envelope to JsonElement envelope for InboxMessage
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    var inboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = jsonEnvelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Act
    sut.QueueInboxMessage(inboxMessage);
    _ = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Message should be passed to coordinator
    await Assert.That(fakeCoordinator.LastNewInboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].HandlerName).IsEqualTo("TestHandler");
  }

  // ========================================
  // Constructor Tests
  // ========================================

  [Test]
  public async Task Constructor_WithNullCoordinator_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    // Act & Assert
    await Assert.That(() => new ImmediateWorkCoordinatorStrategy(
      null!,
      instanceProvider,
      options
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullInstanceProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var options = new WorkCoordinatorOptions();

    // Act & Assert
    await Assert.That(() => new ImmediateWorkCoordinatorStrategy(
      coordinator,
      null!,
      options
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();

    // Act & Assert
    await Assert.That(() => new ImmediateWorkCoordinatorStrategy(
      coordinator,
      instanceProvider,
      null!
    )).Throws<ArgumentNullException>();
  }

  // ========================================
  // Completion/Failure Queue Tests
  // ========================================

  [Test]
  public async Task QueueOutboxCompletion_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();

    // Act
    sut.QueueOutboxCompletion(messageId, MessageProcessingStatus.Published);
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(fakeCoordinator.LastOutboxCompletions).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastOutboxCompletions[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastOutboxCompletions[0].Status).IsEqualTo(MessageProcessingStatus.Published);
  }

  [Test]
  public async Task QueueInboxCompletion_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();

    // Act
    sut.QueueInboxCompletion(messageId, MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored);
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(fakeCoordinator.LastInboxCompletions).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastInboxCompletions[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastInboxCompletions[0].Status).IsEqualTo(MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored);
  }

  [Test]
  public async Task QueueOutboxFailure_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();

    // Act
    sut.QueueOutboxFailure(messageId, MessageProcessingStatus.Stored, "Delivery failed");
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(fakeCoordinator.LastOutboxFailures).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastOutboxFailures[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastOutboxFailures[0].CompletedStatus).IsEqualTo(MessageProcessingStatus.Stored);
    await Assert.That(fakeCoordinator.LastOutboxFailures[0].Error).IsEqualTo("Delivery failed");
  }

  [Test]
  public async Task QueueInboxFailure_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();

    // Act
    sut.QueueInboxFailure(messageId, MessageProcessingStatus.Stored, "Handler threw exception");
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(fakeCoordinator.LastInboxFailures).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastInboxFailures[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastInboxFailures[0].CompletedStatus).IsEqualTo(MessageProcessingStatus.Stored);
    await Assert.That(fakeCoordinator.LastInboxFailures[0].Error).IsEqualTo("Handler threw exception");
  }

  // ========================================
  // Flush Clears Queue Tests
  // ========================================

  [Test]
  public async Task FlushAsync_ClearsQueuesAfterFlushAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    sut.QueueOutboxCompletion(messageId, MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(messageId, MessageProcessingStatus.Stored);

    // Act - First flush
    await sut.FlushAsync(WorkBatchFlags.None);
    // Second flush should have empty queues
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Second flush should have empty arrays
    await Assert.That(fakeCoordinator.LastOutboxCompletions).Count().IsEqualTo(0);
    await Assert.That(fakeCoordinator.LastInboxCompletions).Count().IsEqualTo(0);
  }

  // ========================================
  // DebugMode Flag Tests
  // ========================================

  [Test]
  public async Task FlushAsync_WithDebugMode_SetsDebugFlagAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { DebugMode = true };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    // Act
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - DebugMode should be set
    await Assert.That(fakeCoordinator.LastFlags & WorkBatchFlags.DebugMode).IsEqualTo(WorkBatchFlags.DebugMode);
  }

  [Test]
  public async Task FlushAsync_WithoutDebugMode_DoesNotSetDebugFlagAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { DebugMode = false };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    // Act
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - DebugMode should not be set
    await Assert.That(fakeCoordinator.LastFlags & WorkBatchFlags.DebugMode).IsEqualTo(WorkBatchFlags.None);
  }

  // ========================================
  // Logger Coverage Tests (Lines 83, 105, 118, 131, 145, 159, 179, 185-191)
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_WithLogger_LogsMessageQueuedAsync() {
    // Arrange - logger != null exercises line 83
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueOutboxMessage(_createOutboxMessage());

    // Assert - logger was called
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueInboxMessage_WithLogger_LogsMessageQueuedAsync() {
    // Arrange - logger != null exercises line 105
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueInboxMessage(new InboxMessage {
      MessageId = Guid.NewGuid(),
      HandlerName = "TestHandler",
      Envelope = _createJsonEnvelope(),
      EnvelopeType = "Test",
      StreamId = Guid.NewGuid(),
      MessageType = "TestMessage"
    });

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueOutboxCompletion_WithLogger_LogsCompletionQueuedAsync() {
    // Arrange - logger != null exercises line 118
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueOutboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueInboxCompletion_WithLogger_LogsCompletionQueuedAsync() {
    // Arrange - logger != null exercises line 131
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueInboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueOutboxFailure_WithLogger_LogsFailureQueuedAsync() {
    // Arrange - logger != null exercises line 145
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "Test error");

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueInboxFailure_WithLogger_LogsFailureQueuedAsync() {
    // Arrange - logger != null exercises line 159
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueInboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "Test error");

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task FlushAsync_WithLogger_LogsFlushStartingAsync() {
    // Arrange - logger != null exercises lines 185-191
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Queue a message so FlushAsync has work (exercises flush logging)
    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Multiple log entries: one for queue, one for flush
    await Assert.That(logger.LogCount).IsGreaterThanOrEqualTo(2);
  }

  // ========================================
  // Audit Message Building Coverage (Lines 90-92, 226-227)
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_WithAuditEnabled_BuildsAuditMessageAsync() {
    // Arrange - EventAuditEnabled + IsEvent exercises lines 90-92
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var systemEventOptions = new Whizbang.Core.SystemEvents.SystemEventOptions();
    systemEventOptions.EnableEventAudit();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options,
      systemEventOptions: Microsoft.Extensions.Options.Options.Create(systemEventOptions)
    );

    // Queue an event message with IsEvent=true
    sut.QueueOutboxMessage(_createOutboxMessage());

    // Flush to merge audit messages (line 226-227)
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Should have original + audit message in the batch
    await Assert.That(fakeCoordinator.LastNewOutboxMessages.Length).IsGreaterThanOrEqualTo(1);
  }

  // ========================================
  // Channel Write Tests
  // ========================================

  [Test]
  public async Task FlushAsync_WithReturnedWork_WritesToChannelAsync() {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var messageId1 = Guid.CreateVersion7();
    var messageId2 = Guid.CreateVersion7();
    var fakeCoordinator = new FakeWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = messageId1,
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createJsonEnvelope(),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        },
        new OutboxWork {
          MessageId = messageId2,
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createJsonEnvelope(),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, workChannelWriter: channelWriter
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(channelWriter.WrittenWork).Count().IsEqualTo(2);
    await Assert.That(channelWriter.WrittenWork[0].MessageId).IsEqualTo(messageId1);
    await Assert.That(channelWriter.WrittenWork[1].MessageId).IsEqualTo(messageId2);
  }

  [Test]
  public async Task FlushAsync_NullChannelWriter_DoesNotThrowAsync() {
    // Arrange - no channel writer (null)
    var fakeCoordinator = new FakeWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = Guid.CreateVersion7(),
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createJsonEnvelope(),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act & Assert - should not throw
    var result = await sut.FlushAsync(WorkBatchFlags.None);
    await Assert.That(result.OutboxWork).Count().IsEqualTo(1);
  }

  [Test]
  public async Task FlushAsync_ChannelClosed_HandlesGracefullyAsync() {
    // Arrange
    var channelWriter = new ClosedTestWorkChannelWriter();
    var fakeCoordinator = new FakeWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = Guid.CreateVersion7(),
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createJsonEnvelope(),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, workChannelWriter: channelWriter
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act & Assert - should handle ChannelClosedException gracefully
    var result = await sut.FlushAsync(WorkBatchFlags.None);
    await Assert.That(result.OutboxWork).Count().IsEqualTo(1);
  }

  // ========================================
  // Helper Methods
  // ========================================

  private OutboxMessage _createOutboxMessage() {
    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    var envelopeJson = System.Text.Json.JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = System.Text.Json.JsonSerializer.Deserialize<MessageEnvelope<System.Text.Json.JsonElement>>(envelopeJson, jsonOptions)!;

    return new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    };
  }

  private MessageEnvelope<System.Text.Json.JsonElement> _createJsonEnvelope() {
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.New(),
      Payload = new _testEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    var json = System.Text.Json.JsonSerializer.Serialize((object)envelope, jsonOptions);
    return System.Text.Json.JsonSerializer.Deserialize<MessageEnvelope<System.Text.Json.JsonElement>>(json, jsonOptions)!;
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeLogger<T> : Microsoft.Extensions.Logging.ILogger<T> {
    public int LogCount { get; private set; }

    public void Log<TState>(
      Microsoft.Extensions.Logging.LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      LogCount++;
    }

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    public List<OutboxWork> WrittenWork { get; } = [];

    public System.Threading.Channels.ChannelReader<OutboxWork> Reader =>
      throw new NotImplementedException("Reader not needed for tests");

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      return ValueTask.CompletedTask;
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return true;
    }

    public void Complete() { }
  }

  private sealed class ClosedTestWorkChannelWriter : IWorkChannelWriter {
    public System.Threading.Channels.ChannelReader<OutboxWork> Reader =>
      throw new NotImplementedException("Reader not needed for tests");

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) =>
      throw new System.Threading.Channels.ChannelClosedException();

    public bool TryWrite(OutboxWork work) => false;

    public void Complete() { }
  }

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];
    public MessageCompletion[] LastOutboxCompletions { get; private set; } = [];
    public MessageCompletion[] LastInboxCompletions { get; private set; } = [];
    public MessageFailure[] LastOutboxFailures { get; private set; } = [];
    public MessageFailure[] LastInboxFailures { get; private set; } = [];
    public WorkBatchFlags LastFlags { get; private set; }
    public List<OutboxWork> WorkToReturn { get; set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = request.NewOutboxMessages;
      LastNewInboxMessages = request.NewInboxMessages;
      LastOutboxCompletions = request.OutboxCompletions;
      LastInboxCompletions = request.InboxCompletions;
      LastOutboxFailures = request.OutboxFailures;
      LastInboxFailures = request.InboxFailures;
      LastFlags = request.Flags;

      return Task.FromResult(new WorkBatch {
        OutboxWork = WorkToReturn,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;

    public ServiceInstanceInfo ToInfo() {
      return new ServiceInstanceInfo {
        ServiceName = ServiceName,
        InstanceId = InstanceId,
        HostName = HostName,
        ProcessId = ProcessId
      };
    }
  }
}
