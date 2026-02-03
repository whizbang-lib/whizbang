using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IntervalWorkCoordinatorStrategy - verifies timer-based batching behavior.
/// </summary>
public class IntervalWorkCoordinatorStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // Helper method to create test envelope
  private static TestMessageEnvelope _createTestEnvelope(Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = []
    };
  }

  // ========================================
  // Priority 3 Tests: Interval Strategy
  // ========================================

  [Test]
  public async Task BackgroundTimer_FlushesEveryIntervalAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 100,  // 100ms interval for fast test
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = _createTestEnvelope(messageId);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });

    // Act - Wait for timer to fire (give it 500ms = 5x the interval for reliability under load)
    await Task.Delay(500);

    // Assert - Timer should have flushed the queued message
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Background timer should flush queued messages automatically");

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueuedMessages_BatchedUntilTimerAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,  // 1 second interval (longer to avoid races)
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId1 = _idProvider.NewGuid();
    var messageId2 = _idProvider.NewGuid();

    // Act - Queue two messages quickly (well before timer fires)
    var envelope1 = _createTestEnvelope(messageId1);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId1,
      Destination = "topic1",
      Envelope = envelope1,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId1),
        Hops = []
      }
    });

    await Task.Delay(100);  // Delay between messages (still less than 1s interval)

    var envelope2 = _createTestEnvelope(messageId2);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId2,
      Destination = "topic2",
      Envelope = envelope2,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId2),
        Hops = []
      }
    });

    // Wait for timer to fire (1s interval + buffer)
    await Task.Delay(1500);

    // Assert - Both messages should be batched together in single flush
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Timer should have fired and flushed messages");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages.Length).IsEqualTo(2)
      .Because("Both messages should be batched in the same flush");

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task DisposeAsync_FlushesAndStopsTimerAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,  // 1 second interval
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = _createTestEnvelope(messageId);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });

    // Act - Dispose before timer fires (within 1 second)
    await sut.DisposeAsync();

    // Assert - Disposal should flush queued message immediately
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should flush queued messages immediately");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);

    var callCountBeforeDelay = fakeCoordinator.ProcessWorkBatchCallCount;

    // Wait to verify timer stopped (no additional flushes after disposal)
    await Task.Delay(1500);

    // Assert - Timer should be stopped (no additional flush calls)
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(callCountBeforeDelay)
      .Because("Timer should be stopped after disposal");
  }

  [Test]
  public async Task ManualFlushAsync_DoesNotWaitForTimerAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,  // 5 second interval (long)
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = _createTestEnvelope(messageId);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });

    // Act - Manual flush (should not wait for 5 second timer)
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Manual flush should work immediately (not wait for timer)
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Manual FlushAsync should flush immediately without waiting for timer");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // INBOX MESSAGE TESTS
  // ========================================

  [Test]
  public async Task QueueInboxMessage_ShouldBatchWithOutboxMessagesAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var inboxMessageId = _idProvider.NewGuid();
    var inboxEnvelope = _createTestEnvelope(inboxMessageId);

    // Act - Queue inbox message
    sut.QueueInboxMessage(new InboxMessage {
      MessageId = inboxMessageId,
      HandlerName = "TestHandler",
      Envelope = inboxEnvelope,
      EnvelopeType = "TestEnvelope",
      MessageType = "TestMessage"
    });

    // Manual flush
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].MessageId).IsEqualTo(inboxMessageId);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // COMPLETION AND FAILURE TESTS
  // ========================================

  [Test]
  public async Task QueueOutboxCompletion_ShouldBeIncludedInFlushAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();

    // Act
    sut.QueueOutboxCompletion(messageId, MessageProcessingStatus.Published);
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastOutboxCompletions).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastOutboxCompletions[0].MessageId).IsEqualTo(messageId);

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueInboxCompletion_ShouldBeIncludedInFlushAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();

    // Act
    sut.QueueInboxCompletion(messageId, MessageProcessingStatus.Published);
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastInboxCompletions).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastInboxCompletions[0].MessageId).IsEqualTo(messageId);

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueOutboxFailure_ShouldBeIncludedInFlushAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();

    // Act
    sut.QueueOutboxFailure(messageId, MessageProcessingStatus.Failed, "Test error");
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastOutboxFailures).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastOutboxFailures[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastOutboxFailures[0].Error).IsEqualTo("Test error");

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueInboxFailure_ShouldBeIncludedInFlushAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();

    // Act
    sut.QueueInboxFailure(messageId, MessageProcessingStatus.Failed, "Inbox error");
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastInboxFailures).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastInboxFailures[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastInboxFailures[0].Error).IsEqualTo("Inbox error");

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // EDGE CASE TESTS
  // ========================================

  [Test]
  public async Task FlushAsync_WithNoQueuedOperations_ShouldReturnEmptyBatchAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    // Act - Flush with nothing queued
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Should return empty batch without calling coordinator
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
    await Assert.That(result.InboxWork).Count().IsEqualTo(0);
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0);

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueOutboxMessage_AfterDispose_ShouldThrowObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    await sut.DisposeAsync();

    // Act & Assert
    var messageId = _idProvider.NewGuid();
    var envelope = _createTestEnvelope(messageId);

    await Assert.That(() => sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test",
      Envelope = envelope,
      EnvelopeType = "Test",
      StreamId = _idProvider.NewGuid(),
      IsEvent = false,
      MessageType = "Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] }
    })).ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task FlushAsync_AfterDispose_ShouldThrowObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(async () => await sut.FlushAsync(WorkBatchFlags.None))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task DisposeAsync_CalledMultipleTimes_ShouldNotThrowAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    // Act - Dispose multiple times
    await sut.DisposeAsync();
    await sut.DisposeAsync();
    await sut.DisposeAsync();

    // Assert - Should not throw
  }

  // ========================================
  // CONSTRUCTOR VALIDATION TESTS
  // ========================================

  [Test]
  public async Task Constructor_WithNullCoordinator_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    // Act & Assert
    await Assert.That(() => new IntervalWorkCoordinatorStrategy(
      null!,
      instanceProvider,
      options
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullInstanceProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var options = new WorkCoordinatorOptions();

    // Act & Assert
    await Assert.That(() => new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      null!,
      options
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();

    // Act & Assert
    await Assert.That(() => new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      null!
    )).Throws<ArgumentNullException>();
  }

  // ========================================
  // DEBUG MODE TESTS
  // ========================================

  [Test]
  public async Task FlushAsync_WithDebugMode_SetsDebugFlagAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinatorWithFlags();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      DebugMode = true
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    sut.QueueOutboxCompletion(messageId, MessageProcessingStatus.Published);

    // Act
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - DebugMode flag should be set
    await Assert.That(fakeCoordinator.LastFlags & WorkBatchFlags.DebugMode).IsEqualTo(WorkBatchFlags.DebugMode);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // MORE DISPOSED STATE TESTS
  // ========================================

  [Test]
  public async Task QueueInboxMessage_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { IntervalMilliseconds = 5000 };

    var sut = new IntervalWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, options);
    await sut.DisposeAsync();

    var messageId = _idProvider.NewGuid();
    var envelope = _createTestEnvelope(messageId);

    // Act & Assert
    await Assert.That(() => sut.QueueInboxMessage(new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = "Test",
      MessageType = "Test"
    })).ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueOutboxCompletion_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { IntervalMilliseconds = 5000 };

    var sut = new IntervalWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueOutboxCompletion(_idProvider.NewGuid(), MessageProcessingStatus.Published))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxCompletion_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { IntervalMilliseconds = 5000 };

    var sut = new IntervalWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueInboxCompletion(_idProvider.NewGuid(), MessageProcessingStatus.Published))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueOutboxFailure_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { IntervalMilliseconds = 5000 };

    var sut = new IntervalWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueOutboxFailure(_idProvider.NewGuid(), MessageProcessingStatus.Failed, "error"))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxFailure_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { IntervalMilliseconds = 5000 };

    var sut = new IntervalWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueInboxFailure(_idProvider.NewGuid(), MessageProcessingStatus.Failed, "error"))
      .ThrowsExactly<ObjectDisposedException>();
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];
    public MessageCompletion[] LastOutboxCompletions { get; private set; } = [];
    public MessageCompletion[] LastInboxCompletions { get; private set; } = [];
    public MessageFailure[] LastOutboxFailures { get; private set; } = [];
    public MessageFailure[] LastInboxFailures { get; private set; } = [];

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

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCheckpointCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCheckpointFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }

  private sealed class FakeWorkCoordinatorWithFlags : IWorkCoordinator {
    public WorkBatchFlags LastFlags { get; private set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      LastFlags = request.Flags;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCheckpointCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCheckpointFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
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

  // Test envelope implementation
  private sealed class TestMessageEnvelope : IMessageEnvelope<JsonElement> {
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }
    public JsonElement Payload { get; init; } = JsonDocument.Parse("{}").RootElement;  // Test payload
    object IMessageEnvelope.Payload => Payload;  // Explicit interface implementation

    public void AddHop(MessageHop hop) {
      Hops.Add(hop);
    }

    public DateTimeOffset GetMessageTimestamp() {
      return Hops.Count > 0 ? Hops[0].Timestamp : DateTimeOffset.UtcNow;
    }

    public CorrelationId? GetCorrelationId() {
      return Hops.Count > 0 ? Hops[0].CorrelationId : null;
    }

    public MessageId? GetCausationId() {
      return Hops.Count > 0 ? Hops[0].CausationId : null;
    }

    public JsonElement? GetMetadata(string key) {
      for (var i = Hops.Count - 1; i >= 0; i--) {
        if (Hops[i].Type == HopType.Current && Hops[i].Metadata?.ContainsKey(key) == true) {
          return Hops[i].Metadata![key];
        }
      }
      return null;
    }
  }
}
