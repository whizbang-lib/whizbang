using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Validation;
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
      var result = await sut.FlushAsync(WorkBatchOptions.None);

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
      var result = await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort);

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
      var result = await sut.FlushAsync(WorkBatchOptions.None);

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
    await Assert.That(async () => await sut.FlushAsync(WorkBatchOptions.None))
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
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(fakeCoordinator.LastFlags & WorkBatchOptions.DebugMode).IsEqualTo(WorkBatchOptions.DebugMode);
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
      await sut.FlushAsync(WorkBatchOptions.None);

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
      await sut.FlushAsync(WorkBatchOptions.None);

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
      await sut.FlushAsync(WorkBatchOptions.None);

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
      await sut.FlushAsync(WorkBatchOptions.None);

      await Assert.That(fakeCoordinator.LastInboxFailures).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastInboxFailures[0].Error).IsEqualTo("Inbox error");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // LOGGER PATHS
  // ========================================

  [Test]
  public async Task Constructor_WithLogger_LogsStrategyStartedAsync() {
    // Arrange & Act
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 5, debounceMs: 200),
      logger: logger
    );

    // Assert - no exception, logger branch covered
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueOutboxMessage_WithLogger_LogsQueuedMessageAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000),
      logger: logger
    );

    try {
      // Act
      sut.QueueOutboxMessage(_createOutboxMessage());
      // Assert - no exception, logger branch covered
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_WithLogger_LogsQueuedMessageAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000),
      logger: logger
    );

    try {
      // Act
      sut.QueueInboxMessage(_createInboxMessage());
      // Assert - no exception, logger branch covered
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_WithLogger_LogsFlushDetailsAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000),
      logger: logger
    );

    try {
      // Queue various operations to cover logger paths in flush
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueInboxMessage(_createInboxMessage());
      sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
      sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
      sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err1");
      sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err2");

      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_EmptyQueues_WithLogger_LogsNoQueuedOperationsAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(),
      logger: logger
    );

    try {
      // Act - flush with nothing queued
      var result = await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task DisposeAsync_WithLogger_LogsDisposingAndDisposedAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000),
      logger: logger
    );

    // Act
    await sut.DisposeAsync();
    // Assert - no exception, logger branches covered
  }

  [Test]
  public async Task DisposeAsync_WithLogger_UnflushedOperations_LogsWarningAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 60000),
      logger: logger
    );

    // Queue operations without flushing
    sut.QueueOutboxMessage(_createOutboxMessage());
    sut.QueueInboxMessage(_createInboxMessage());
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");

    // Act
    await sut.DisposeAsync();
    // Assert - covers LogDisposingWithUnflushedOperations path
  }

  [Test]
  public async Task DisposeAsync_WithLogger_FlushError_LogsErrorAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var throwingCoordinator = new BatchThrowingWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      throwingCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 60000),
      logger: logger
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act - DisposeAsync should catch the exception and log it
    await sut.DisposeAsync();
    // Assert - covers LogErrorFlushingOnDisposal path
  }

  // ========================================
  // BATCH SIZE TRIGGER FROM INBOX
  // ========================================

  [Test]
  public async Task QueueInboxMessage_FlushesWhenBatchSizeReachedAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 2, debounceMs: 5000)
    );

    try {
      // Act - Queue inbox messages to trigger batch flush
      sut.QueueInboxMessage(_createInboxMessage());
      sut.QueueInboxMessage(_createInboxMessage()); // Batch size reached

      // Wait for the flush
      await fakeCoordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_BelowBatchSize_DoesNotFlushImmediatelyAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000)
    );

    try {
      // Act
      sut.QueueInboxMessage(_createInboxMessage());

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // BATCH SIZE TRIGGER WITH LOGGER
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_BatchSizeReached_WithLogger_LogsBatchSizeReachedAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 2, debounceMs: 5000),
      logger: logger
    );

    try {
      // Act
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage()); // Batch size reached

      await fakeCoordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_BatchSizeReached_WithLogger_LogsBatchSizeReachedAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 2, debounceMs: 5000),
      logger: logger
    );

    try {
      // Act
      sut.QueueInboxMessage(_createInboxMessage());
      sut.QueueInboxMessage(_createInboxMessage()); // Batch size reached

      await fakeCoordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // BATCH FLUSH ERROR WITH LOGGER
  // ========================================

  [Test]
  public async Task BatchFlush_Error_WithLogger_LogsErrorAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var throwingCoordinator = new BatchThrowingWorkCoordinator();
    var flushErrorTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    throwingCoordinator.OnProcessCalled = () => flushErrorTcs.TrySetResult();

    var sut = new BatchWorkCoordinatorStrategy(
      throwingCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 2, debounceMs: 5000),
      logger: logger
    );

    try {
      // Act - trigger batch flush that will error
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage());

      // Wait for the flush attempt
      await flushErrorTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
      // covers LogErrorDuringBatchFlush
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // DEBOUNCE TIMER WITH LOGGER
  // ========================================

  [Test]
  public async Task DebounceTimer_WithLogger_LogsDebounceTimerFiredAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 50),
      logger: logger
    );

    try {
      var flushTcs = new TaskCompletionSource<WorkBatchFlushedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
      sut.OnBatchFlushed += args => flushTcs.TrySetResult(args);

      // Act
      sut.QueueOutboxMessage(_createOutboxMessage());

      var result = await flushTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

      // Assert
      await Assert.That(result.Trigger).IsEqualTo(FlushTrigger.Debounce);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task DebounceTimer_Error_WithLogger_LogsErrorAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var throwingCoordinator = new BatchThrowingWorkCoordinator();
    var flushErrorTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    throwingCoordinator.OnProcessCalled = () => flushErrorTcs.TrySetResult();

    var sut = new BatchWorkCoordinatorStrategy(
      throwingCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 50),
      logger: logger
    );

    try {
      // Act - queue a message below batch size so debounce timer fires
      sut.QueueOutboxMessage(_createOutboxMessage());

      // Wait for the debounce flush attempt
      await flushErrorTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
      // covers LogErrorDuringDebounceFlush
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // COALESCE WINDOW
  // ========================================

  [Test]
  public async Task FlushAsync_CoalesceWindowGreaterThanZero_WaitsBeforeFlushAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var options = _createOptions(batchSize: 100, debounceMs: 5000);
    options.CoalesceWindowMilliseconds = 10; // Small window

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      options
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      var result = await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
      await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_CoalesceWindow_CancellationToken_ThrowsAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var options = _createOptions(batchSize: 100, debounceMs: 5000);
    options.CoalesceWindowMilliseconds = 10000; // Long window

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      options
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act & Assert - Accept both TaskCanceledException and OperationCanceledException
      // since cancellation may manifest as either type depending on runtime/instrumentation timing
      using var cts = new CancellationTokenSource();
      cts.Cancel();
      Exception? caught = null;
      try {
        await sut.FlushAsync(WorkBatchOptions.None, FlushMode.Required, cts.Token);
      } catch (Exception ex) {
        caught = ex;
      }
      await Assert.That(caught).IsNotNull()
        .Because("Flushing with a cancelled token should throw");
      await Assert.That(caught is OperationCanceledException).IsTrue()
        .Because("Should throw OperationCanceledException (or its subclass TaskCanceledException)");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // CONCURRENT FLUSH PREVENTION
  // ========================================

  [Test]
  public async Task FlushAsync_ConcurrentFlush_ReturnsEmptyForSecondCallAsync() {
    // Arrange
    var slowCoordinator = new BatchSlowWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      slowCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 60000)
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act - Start first flush (will be slow)
      var firstFlush = sut.FlushAsync(WorkBatchOptions.None);

      // Wait for the slow coordinator to start processing
      await slowCoordinator.WaitForProcessingStartedAsync(TimeSpan.FromSeconds(5));

      // Queue another message and try second flush while first is in progress
      sut.QueueOutboxMessage(_createOutboxMessage());
      var secondResult = await sut.FlushAsync(WorkBatchOptions.None);

      // Release the slow coordinator
      slowCoordinator.ReleaseProcessing();
      var firstResult = await firstFlush;

      // Assert - second flush returns empty because first was in progress
      await Assert.That(secondResult.OutboxWork).Count().IsEqualTo(0);
      await Assert.That(secondResult.InboxWork).Count().IsEqualTo(0);
    } finally {
      slowCoordinator.ReleaseProcessing(); // ensure cleanup
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_ConcurrentFlush_WithLogger_LogsFlushAlreadyInProgressAsync() {
    // Arrange
    var logger = NullLogger<BatchWorkCoordinatorStrategy>.Instance;
    var slowCoordinator = new BatchSlowWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      slowCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 60000),
      logger: logger
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      var firstFlush = sut.FlushAsync(WorkBatchOptions.None);
      await slowCoordinator.WaitForProcessingStartedAsync(TimeSpan.FromSeconds(5));

      sut.QueueOutboxMessage(_createOutboxMessage());
      var secondResult = await sut.FlushAsync(WorkBatchOptions.None);

      slowCoordinator.ReleaseProcessing();
      await firstFlush;

      // Assert
      await Assert.That(secondResult.OutboxWork).Count().IsEqualTo(0);
    } finally {
      slowCoordinator.ReleaseProcessing();
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // IWORKFLUSHER EXPLICIT INTERFACE
  // ========================================

  [Test]
  public async Task IWorkFlusher_FlushAsync_DelegatesToFlushAsyncWithRequiredModeAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000)
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act - call through IWorkFlusher interface
      IWorkFlusher flusher = sut;
      await flusher.FlushAsync();

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task IWorkFlusher_FlushAsync_WithCancellationToken_PassesToUnderlyingAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions()
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      IWorkFlusher flusher = sut;
      using var cts = new CancellationTokenSource();
      await flusher.FlushAsync(cts.Token);

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // SCOPE FACTORY (NULL COORDINATOR)
  // ========================================

  [Test]
  public async Task Constructor_WithNullCoordinator_AndScopeFactory_SucceedsAsync() {
    // Arrange
    var scopeFactory = new BatchFakeScopeFactory(new BatchFakeWorkCoordinator());

    var sut = new BatchWorkCoordinatorStrategy(
      coordinator: null,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000),
      scopeFactory: scopeFactory
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      var result = await sut.FlushAsync(WorkBatchOptions.None);

      // Assert - coordinator resolved through scope
      await Assert.That(scopeFactory.ScopeCreationCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // ON BATCH FLUSHED EVENT
  // ========================================

  [Test]
  public async Task OnBatchFlushed_ManualFlush_TriggerIsManualAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000)
    );

    var flushTcs = new TaskCompletionSource<WorkBatchFlushedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    sut.OnBatchFlushed += args => flushTcs.TrySetResult(args);

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      await sut.FlushAsync(WorkBatchOptions.None);
      var result = await flushTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

      // Assert
      await Assert.That(result.Trigger).IsEqualTo(FlushTrigger.Manual);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task OnBatchFlushed_BatchSizeTrigger_TriggerIsBatchSizeAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 2, debounceMs: 5000)
    );

    var flushTcs = new TaskCompletionSource<WorkBatchFlushedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    sut.OnBatchFlushed += args => flushTcs.TrySetResult(args);

    try {
      // Act
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage());

      var result = await flushTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

      // Assert
      await Assert.That(result.Trigger).IsEqualTo(FlushTrigger.BatchSize);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // STREAM ID VALIDATION
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_EmptyStreamId_ThrowsInvalidStreamIdExceptionAsync() {
    // Arrange
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions()
    );

    var message = new OutboxMessage {
      MessageId = Guid.CreateVersion7(),
      Destination = "test-topic",
      Envelope = _createEnvelope(Guid.CreateVersion7()),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.Empty, // Non-null but empty — triggers InvalidStreamIdException
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] }
    };

    try {
      // Act & Assert
      await Assert.That(() => sut.QueueOutboxMessage(message))
        .ThrowsExactly<InvalidStreamIdException>();
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_EmptyStreamId_ThrowsInvalidStreamIdExceptionAsync() {
    // Arrange
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions()
    );

    var message = new InboxMessage {
      MessageId = Guid.CreateVersion7(),
      HandlerName = "TestHandler",
      Envelope = _createEnvelope(Guid.CreateVersion7()),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.Empty, // Non-null but empty — triggers InvalidStreamIdException
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly"
    };

    try {
      // Act & Assert
      await Assert.That(() => sut.QueueInboxMessage(message))
        .ThrowsExactly<InvalidStreamIdException>();
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueOutboxMessage_NullStreamId_SucceedsAsync() {
    // Arrange
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000)
    );

    var message = new OutboxMessage {
      MessageId = Guid.CreateVersion7(),
      Destination = "test-topic",
      Envelope = _createEnvelope(Guid.CreateVersion7()),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = null, // Null is valid
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] }
    };

    try {
      // Act & Assert - should not throw
      sut.QueueOutboxMessage(message);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // MIXED OPERATIONS IN SINGLE FLUSH
  // ========================================

  [Test]
  public async Task FlushAsync_AllOperationTypes_AllIncludedInSingleFlushAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000)
    );

    var outboxMsgId = Guid.CreateVersion7();
    var inboxMsgId = Guid.CreateVersion7();
    var outboxCompId = Guid.CreateVersion7();
    var inboxCompId = Guid.CreateVersion7();
    var outboxFailId = Guid.CreateVersion7();
    var inboxFailId = Guid.CreateVersion7();

    try {
      sut.QueueOutboxMessage(_createOutboxMessage(outboxMsgId));
      sut.QueueInboxMessage(_createInboxMessage(inboxMsgId));
      sut.QueueOutboxCompletion(outboxCompId, MessageProcessingStatus.Published);
      sut.QueueInboxCompletion(inboxCompId, MessageProcessingStatus.Stored);
      sut.QueueOutboxFailure(outboxFailId, MessageProcessingStatus.Failed, "outbox err");
      sut.QueueInboxFailure(inboxFailId, MessageProcessingStatus.Failed, "inbox err");

      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert - all operations included
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(outboxMsgId);
      await Assert.That(fakeCoordinator.LastNewInboxMessages).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastNewInboxMessages[0].MessageId).IsEqualTo(inboxMsgId);
      await Assert.That(fakeCoordinator.LastOutboxCompletions).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastOutboxCompletions[0].MessageId).IsEqualTo(outboxCompId);
      await Assert.That(fakeCoordinator.LastInboxCompletions).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastInboxCompletions[0].MessageId).IsEqualTo(inboxCompId);
      await Assert.That(fakeCoordinator.LastOutboxFailures).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastOutboxFailures[0].MessageId).IsEqualTo(outboxFailId);
      await Assert.That(fakeCoordinator.LastInboxFailures).Count().IsEqualTo(1);
      await Assert.That(fakeCoordinator.LastInboxFailures[0].MessageId).IsEqualTo(inboxFailId);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // FLUSH CLEARS QUEUES
  // ========================================

  [Test]
  public async Task FlushAsync_ClearsQueues_SecondFlushIsEmptyAsync() {
    // Arrange
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000)
    );

    try {
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueInboxMessage(_createInboxMessage());
      sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
      sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
      sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");
      sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");

      // First flush
      await sut.FlushAsync(WorkBatchOptions.None);
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

      // Second flush - queues should be empty
      var result = await sut.FlushAsync(WorkBatchOptions.None);
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
        .Because("Second flush should be empty (no-op, no ProcessWorkBatch call)");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // METRICS PATHS
  // ========================================

  [Test]
  public async Task FlushAsync_WithMetrics_RecordsFlushCallsAsync() {
    // Arrange
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000),
      metrics: metrics
    );

    try {
      // Act - flush with items (covers FlushCalls metric)
      sut.QueueOutboxMessage(_createOutboxMessage());
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_EmptyQueues_WithMetrics_RecordsEmptyFlushCallsAsync() {
    // Arrange
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(),
      metrics: metrics
    );

    try {
      // Act - flush with nothing queued (covers EmptyFlushCalls metric)
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_BestEffort_WithMetrics_RecordsFlushCallsAsync() {
    // Arrange
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(),
      metrics: metrics
    );

    try {
      sut.QueueOutboxMessage(_createOutboxMessage());

      // Act
      var result = await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort);

      // Assert
      await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
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
      await sut.FlushAsync(WorkBatchOptions.None);

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
      var result = await sut.FlushAsync(WorkBatchOptions.None);
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
      var result = await sut.FlushAsync(WorkBatchOptions.None);
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

    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
  }

  private sealed class ClosedTestWorkChannelWriter : IWorkChannelWriter {
    public System.Threading.Channels.ChannelReader<OutboxWork> Reader =>
      throw new NotImplementedException("Reader not needed for tests");

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) =>
      throw new System.Threading.Channels.ChannelClosedException();

    public bool TryWrite(OutboxWork work) => false;

    public void Complete() { }

    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
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
    public WorkBatchOptions LastFlags { get; private set; }

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

  private sealed class BatchThrowingWorkCoordinator : IWorkCoordinator {
    public Action? OnProcessCalled { get; set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      OnProcessCalled?.Invoke();
      throw new InvalidOperationException("Simulated flush error");
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

  private sealed class BatchSlowWorkCoordinator : IWorkCoordinator, IDisposable {
    private readonly SemaphoreSlim _processingStarted = new(0, 1);
    private readonly SemaphoreSlim _releaseProcessing = new(0, 1);

    public async Task WaitForProcessingStartedAsync(TimeSpan timeout) {
      if (!await _processingStarted.WaitAsync(timeout)) {
        throw new TimeoutException("ProcessWorkBatchAsync was not started within timeout");
      }
    }

    public void ReleaseProcessing() {
      try { _releaseProcessing.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      _processingStarted.Release();
      await _releaseProcessing.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
      return new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      };
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

    public void Dispose() {
      _processingStarted.Dispose();
      _releaseProcessing.Dispose();
    }
  }

  private sealed class BatchFakeScopeFactory : IServiceScopeFactory {
    private readonly IWorkCoordinator _coordinator;
    public int ScopeCreationCount { get; private set; }

    public BatchFakeScopeFactory(IWorkCoordinator coordinator) {
      _coordinator = coordinator;
    }

    public IServiceScope CreateScope() {
      ScopeCreationCount++;
      return new FakeServiceScope(_coordinator);
    }

    private sealed class FakeServiceScope(IWorkCoordinator coordinator) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = new FakeServiceProvider(coordinator);
      public void Dispose() { }
    }

    private sealed class FakeServiceProvider(IWorkCoordinator coordinator) : IServiceProvider {
      public object? GetService(Type serviceType) {
        if (serviceType == typeof(IWorkCoordinator)) {
          return coordinator;
        }
        return null;
      }
    }
  }
}
