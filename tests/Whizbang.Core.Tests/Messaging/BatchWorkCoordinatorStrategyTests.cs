using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for BatchWorkCoordinatorStrategy - verifies count-based and debounce-based flush behavior.
/// </summary>
public class BatchWorkCoordinatorStrategyTests {

  private static MessageEnvelope<JsonElement> _createEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = []
    };
  }

  private static OutboxMessage _createOutboxMessage(Guid? messageId = null, string destination = "test-topic") {
    var id = messageId ?? Guid.CreateVersion7();
    return new OutboxMessage {
      MessageId = id,
      Destination = destination,
      Envelope = _createEnvelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(id),
        Hops = []
      }
    };
  }

  private static InboxMessage _createInboxMessage(Guid? messageId = null, string handlerName = "TestHandler") {
    var id = messageId ?? Guid.CreateVersion7();
    return new InboxMessage {
      MessageId = id,
      HandlerName = handlerName,
      Envelope = _createEnvelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly"
    };
  }

  private static WorkCoordinatorOptions _createOptions(int batchSize = 5, int debounceMs = 200) {
    return new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Batch,
      BatchSize = batchSize,
      IntervalMilliseconds = debounceMs,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };
  }

  // ========================================
  // BATCH SIZE TRIGGER TESTS
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_FlushesWhenBatchSizeReachedAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 3, debounceMs: 5000);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    try {
      // Act - Queue exactly batch size messages
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage()); // Should trigger flush

      // Wait for the flush to complete (signal-based)
      await fakeCoordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

      // Assert - Batch size threshold should trigger flush
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
        .Because("Batch size threshold should trigger immediate flush");
      await Assert.That(fakeCoordinator.TotalOutboxMessagesReceived).IsEqualTo(3)
        .Because("All 3 messages should be flushed together");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueOutboxMessage_DoesNotFlushBelowBatchSizeAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 10, debounceMs: 5000);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    try {
      // Subscribe to flush event to detect when flush occurs
      var flushTcs = new TaskCompletionSource<WorkBatchFlushedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
      sut.OnBatchFlushed += args => flushTcs.TrySetResult(args);

      // Act - Queue fewer than batch size
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage());

      // Assert - No batch-size flush should have occurred (debounce is 5000ms, batch is 10)
      // The only flush will come from DisposeAsync
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
        .Because("Below batch size should not trigger flush");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_CountsTowardBatchSizeAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 3, debounceMs: 5000);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    try {
      // Act - Mix of outbox and inbox messages
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueInboxMessage(_createInboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage()); // Total = 3, should trigger flush

      // Wait for the flush to complete (signal-based)
      await fakeCoordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
        .Because("Inbox + outbox messages should both count toward batch size");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // DEBOUNCE TIMER TESTS
  // ========================================

  [Test]
  public async Task DebounceTimer_FlushesAfterQuietPeriodAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 150); // Low debounce, high batch size

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    try {
      // Act - Queue 1 message (below batch size)
      sut.QueueOutboxMessage(_createOutboxMessage());

      // Wait for debounce timer to fire (signal-based)
      await fakeCoordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

      // Assert - Debounce timer should have flushed the partial batch
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
        .Because("Debounce timer should flush after quiet period");
      await Assert.That(fakeCoordinator.TotalOutboxMessagesReceived).IsEqualTo(1)
        .Because("The single message should be flushed by debounce");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task DebounceTimer_ResetsOnEachQueueAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 50);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    try {
      // Subscribe to flush event
      var flushTcs = new TaskCompletionSource<WorkBatchFlushedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
      sut.OnBatchFlushed += args => flushTcs.TrySetResult(args);

      // Act - Queue two messages in rapid succession (both within debounce window)
      // Debounce timer resets on each queue, so both should be batched together
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage());

      // Wait for debounce flush via signal (debounce is 50ms, generous timeout for thread pool starvation)
      var flushedArgs = await flushTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

      // Assert - Both messages batched together proves debounce reset worked
      // (If timer didn't reset, first message would flush alone)
      await Assert.That(flushedArgs.Trigger).IsEqualTo(FlushTrigger.Debounce)
        .Because("Flush should be triggered by debounce timer");
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
        .Because("Flush should occur after messages stop arriving");
      await Assert.That(fakeCoordinator.TotalOutboxMessagesReceived).IsEqualTo(2)
        .Because("Both messages should be batched together (debounce reset)");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // PRIORITY TESTS
  // ========================================

  [Test]
  public async Task BatchSize_TakesPriorityOverDebounceAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 2, debounceMs: 5000); // Long debounce, low batch size

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    try {
      // Act - Queue batch size worth of messages quickly
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage()); // Batch size reached

      // Wait for flush (signal-based)
      await fakeCoordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

      // Assert - Should flush immediately, not wait for debounce
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
        .Because("Batch size should trigger immediate flush without waiting for debounce");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // DISPOSE AND MANUAL FLUSH TESTS
  // ========================================

  [Test]
  public async Task DisposeAsync_FlushesRemainingMessagesAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 5000);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    // Act - Dispose should flush remaining
    await sut.DisposeAsync();

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should flush queued messages");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task ManualFlushAsync_DoesNotWaitForTimerOrBatchAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 5000);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    try {
      // Act - Manual flush should work immediately
      var result = await sut.FlushAsync(WorkBatchFlags.None);

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
        .Because("Manual FlushAsync should flush immediately");
      await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // BEST EFFORT FLUSH MODE
  // ========================================

  [Test]
  public async Task FlushAsync_BestEffort_DefersToTriggersAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 5000);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act - BestEffort should return empty and defer
      var result = await sut.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

      // Assert
      await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
        .Because("BestEffort should defer flush to batch/debounce triggers");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // EDGE CASE TESTS
  // ========================================

  [Test]
  public async Task FlushAsync_WithNoQueuedOperations_ReturnsEmptyAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    try {
      // Act
      var result = await sut.FlushAsync(WorkBatchFlags.None);

      // Assert
      await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueOutboxMessage_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueOutboxMessage(_createOutboxMessage()))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task FlushAsync_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();

    var sut = new BatchWorkCoordinatorStrategy(
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
  public async Task DisposeAsync_CalledMultipleTimes_DoesNotThrowAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();

    var sut = new BatchWorkCoordinatorStrategy(
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

  [Test]
  public async Task Constructor_WithNullCoordinatorAndNullScopeFactory_ThrowsAsync() {
    // Arrange
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();

    // Act & Assert
    await Assert.That(() => new BatchWorkCoordinatorStrategy(
      coordinator: null,
      instanceProvider,
      options,
      scopeFactory: null
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullInstanceProvider_ThrowsAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var options = _createOptions();

    // Act & Assert
    await Assert.That(() => new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      null!,
      options
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();

    // Act & Assert
    await Assert.That(() => new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      null!
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task FlushAsync_WithDebugMode_SetsDebugFlagAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinatorWithFlags();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();
    options.DebugMode = true;

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);

    try {
      // Act
      await sut.FlushAsync(WorkBatchFlags.None);

      // Assert
      await Assert.That(fakeCoordinator.LastFlags & WorkBatchFlags.DebugMode).IsEqualTo(WorkBatchFlags.DebugMode);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // COMPLETION AND FAILURE QUEUE TESTS
  // ========================================

  [Test]
  public async Task QueueOutboxCompletion_IncludedInFlushAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = Guid.CreateVersion7();

    try {
      sut.QueueOutboxCompletion(messageId, MessageProcessingStatus.Published);
      await sut.FlushAsync(WorkBatchFlags.None);

      await Assert.That(fakeCoordinator.LastOutboxCompletions).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastOutboxCompletions[0].MessageId).IsEqualTo(messageId);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxCompletion_IncludedInFlushAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = Guid.CreateVersion7();

    try {
      sut.QueueInboxCompletion(messageId, MessageProcessingStatus.Stored);
      await sut.FlushAsync(WorkBatchFlags.None);

      await Assert.That(fakeCoordinator.LastInboxCompletions).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastInboxCompletions[0].MessageId).IsEqualTo(messageId);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueOutboxFailure_IncludedInFlushAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = Guid.CreateVersion7();

    try {
      sut.QueueOutboxFailure(messageId, MessageProcessingStatus.Failed, "Test error");
      await sut.FlushAsync(WorkBatchFlags.None);

      await Assert.That(fakeCoordinator.LastOutboxFailures).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastOutboxFailures[0].Error).IsEqualTo("Test error");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxFailure_IncludedInFlushAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions();

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = Guid.CreateVersion7();

    try {
      sut.QueueInboxFailure(messageId, MessageProcessingStatus.Failed, "Inbox error");
      await sut.FlushAsync(WorkBatchFlags.None);

      await Assert.That(fakeCoordinator.LastInboxFailures).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastInboxFailures[0].Error).IsEqualTo("Inbox error");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // DISPOSED STATE TESTS
  // ========================================

  [Test]
  public async Task QueueInboxMessage_AfterDispose_ThrowsAsync() {
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(), new BatchFakeInstanceProvider(), _createOptions());
    await sut.DisposeAsync();

    await Assert.That(() => sut.QueueInboxMessage(_createInboxMessage()))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueOutboxCompletion_AfterDispose_ThrowsAsync() {
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(), new BatchFakeInstanceProvider(), _createOptions());
    await sut.DisposeAsync();

    await Assert.That(() => sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxCompletion_AfterDispose_ThrowsAsync() {
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(), new BatchFakeInstanceProvider(), _createOptions());
    await sut.DisposeAsync();

    await Assert.That(() => sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueOutboxFailure_AfterDispose_ThrowsAsync() {
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(), new BatchFakeInstanceProvider(), _createOptions());
    await sut.DisposeAsync();

    await Assert.That(() => sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err"))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxFailure_AfterDispose_ThrowsAsync() {
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(), new BatchFakeInstanceProvider(), _createOptions());
    await sut.DisposeAsync();

    await Assert.That(() => sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err"))
      .ThrowsExactly<ObjectDisposedException>();
  }

  // ========================================
  // Channel Write Tests
  // ========================================

  [Test]
  public async Task FlushAsync_WithReturnedWork_WritesToChannelAsync() {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var messageId1 = Guid.CreateVersion7();
    var fakeCoordinator = new BatchFakeWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = messageId1,
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createEnvelope(messageId1),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, workChannelWriter: channelWriter
    );

    try {
      sut.QueueOutboxMessage(_createOutboxMessage());

      // Act
      await sut.FlushAsync(WorkBatchFlags.None);

      // Assert
      await Assert.That(channelWriter.WrittenWork).Count().IsEqualTo(1);
      await Assert.That(channelWriter.WrittenWork[0].MessageId).IsEqualTo(messageId1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_NullChannelWriter_DoesNotThrowAsync() {
    // Arrange - no channel writer
    var fakeCoordinator = new BatchFakeWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = Guid.CreateVersion7(),
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createEnvelope(Guid.CreateVersion7()),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options
    );

    try {
      sut.QueueOutboxMessage(_createOutboxMessage());

      // Act & Assert - should not throw
      var result = await sut.FlushAsync(WorkBatchFlags.None);
      await Assert.That(result.OutboxWork).Count().IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_ChannelClosed_HandlesGracefullyAsync() {
    // Arrange
    var channelWriter = new ClosedTestWorkChannelWriter();
    var fakeCoordinator = new BatchFakeWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = Guid.CreateVersion7(),
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createEnvelope(Guid.CreateVersion7()),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, workChannelWriter: channelWriter
    );

    try {
      sut.QueueOutboxMessage(_createOutboxMessage());

      // Act & Assert - should handle gracefully
      var result = await sut.FlushAsync(WorkBatchFlags.None);
      await Assert.That(result.OutboxWork).Count().IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
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

  private sealed class BatchFakeWorkCoordinator : IWorkCoordinator, IDisposable {
    private readonly SemaphoreSlim _flushSignal = new(0, int.MaxValue);
    public int ProcessWorkBatchCallCount { get; private set; }
    public int TotalOutboxMessagesReceived { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];
    public MessageCompletion[] LastOutboxCompletions { get; private set; } = [];
    public MessageCompletion[] LastInboxCompletions { get; private set; } = [];
    public MessageFailure[] LastOutboxFailures { get; private set; } = [];
    public MessageFailure[] LastInboxFailures { get; private set; } = [];
    public List<OutboxWork> WorkToReturn { get; set; } = [];

    public void Dispose() => _flushSignal.Dispose();

    /// <summary>
    /// Waits for at least one call to ProcessWorkBatchAsync.
    /// </summary>
    public async Task WaitForFlushAsync(TimeSpan timeout) {
      if (!await _flushSignal.WaitAsync(timeout)) {
        throw new TimeoutException("ProcessWorkBatchAsync was not called within timeout");
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      TotalOutboxMessagesReceived += request.NewOutboxMessages.Length;
      LastNewOutboxMessages = request.NewOutboxMessages;
      LastNewInboxMessages = request.NewInboxMessages;
      LastOutboxCompletions = request.OutboxCompletions;
      LastInboxCompletions = request.InboxCompletions;
      LastOutboxFailures = request.OutboxFailures;
      LastInboxFailures = request.InboxFailures;
      _flushSignal.Release();

      return Task.FromResult(new WorkBatch {
        OutboxWork = WorkToReturn,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class BatchFakeWorkCoordinatorWithFlags : IWorkCoordinator {
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
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class BatchFakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "BatchTestService";
    public string HostName => "test-host";
    public int ProcessId => 12345;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
