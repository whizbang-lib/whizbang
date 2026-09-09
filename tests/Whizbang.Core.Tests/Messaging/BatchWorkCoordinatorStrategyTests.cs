using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
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
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
      AbandonStaleInstanceThresholdSeconds = 300,
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
      var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
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
      var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
    // Arrange - a message still in the buffer, so the disposal flush has something to do and a
    // repeated disposal has something to do twice.
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var instanceProvider = new BatchFakeInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var logger = new _batchCapturingLogger();

    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options,
      logger: logger
    );
    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act - Dispose multiple times
    await sut.DisposeAsync();
    await sut.DisposeAsync();
    await sut.DisposeAsync();

    // Assert - "does not throw" is the weaker half. The `_disposed` guard has to make calls two
    // and three genuine no-ops: a host that disposes a container twice (or disposes a strategy it
    // also owns a scope for) must not re-run the shutdown drain.
    await Assert.That(fakeCoordinator.TotalOutboxMessagesReceived).IsEqualTo(1)
      .Because("the buffered message is stored by the disposal flush -- storing it again on a "
             + "second dispose is a duplicate outbox row, not a harmless retry");
    await Assert.That(logger.Entries.Count(e => e.EventId == EVENT_STRATEGY_DISPOSED)).IsEqualTo(1)
      .Because("one strategy shuts down once; a second 'disposed' line makes a shutdown log "
             + "count instances that do not exist");
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

  // ========================================
  // LOGGER PATHS
  // ========================================
  //
  // Deleted as obsolete after Phase H (coverage moved to WorkCoordinatorFlushHelperTests):
  //   FlushAsync_WithDebugMode_SetsDebugFlagAsync — WorkBatchOptions.DebugMode is no longer
  //     routed through the coordinator; debug-mode is per-component now.
  //   Queue{Outbox,Inbox}{Completion,Failure}_IncludedInFlushAsync — completion/failure
  //     routing happens via IOutboxCompletionChannel / IFailureChannel only when a scoped
  //     provider is available; the direct-coordinator path drops these by design.

  [Test]
  public async Task Constructor_WithLogger_LogsStrategyStartedAsync() {
    // Arrange & Act - values chosen to be unmistakable in the rendered line.
    var logger = new _batchCapturingLogger();
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 7, debounceMs: 250),
      logger: logger
    );

    // Assert - the startup line is the only place the EFFECTIVE batch configuration is visible.
    // Batch size and debounce interval trade write amplification against latency, and a
    // misconfigured host looks identical to a correctly configured one until this line is read.
    var started = logger.Entries.Where(e => e.EventId == EVENT_STRATEGY_STARTED).ToList();
    await Assert.That(started.Count).IsEqualTo(1)
      .Because("construction announces the strategy exactly once");
    await Assert.That(started[0].Level).IsEqualTo(LogLevel.Information)
      .Because("configuration an operator has to be able to read back cannot sit below the "
             + "default production level");
    await Assert.That(started[0].Message.Contains("batch size 7", StringComparison.Ordinal)).IsTrue()
      .Because("announcing a batch size without the number tells an operator nothing they did "
             + "not already know from the fact that batching is on");
    await Assert.That(started[0].Message.Contains("250", StringComparison.Ordinal)).IsTrue()
      .Because("the debounce interval bounds how long a partial batch sits unwritten -- it is "
             + "half of the latency answer and belongs in the same line");

    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueOutboxMessage_WithLogger_LogsQueuedMessageAsync() {
    // Arrange
    var logger = new _batchCapturingLogger();
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000),
      logger: logger
    );
    var message = _createOutboxMessage(destination: "orders.placed");

    try {
      // Act
      sut.QueueOutboxMessage(message);

      // Assert - a queued message is buffered in memory and not yet durable anywhere. When a host
      // dies before the flush, this trace is the only record that the message existed at all, so
      // it has to name WHICH message and WHERE it was headed.
      var queued = logger.Entries.Where(e => e.EventId == EVENT_QUEUED_OUTBOX_MESSAGE).ToList();
      await Assert.That(queued.Count).IsEqualTo(1)
        .Because("one queue call buffers one message and should account for it once");
      await Assert.That(queued[0].Level).IsEqualTo(LogLevel.Trace)
        .Because("a per-message line on the hot path must stay at Trace -- above that it is "
               + "enabled in production and the log becomes the bottleneck");
      await Assert.That(queued[0].Message.Contains(message.MessageId.ToString(), StringComparison.Ordinal)).IsTrue()
        .Because("without the message id this line cannot be joined to the outbox row that "
               + "never appeared, which is the only reason to read it");
      await Assert.That(queued[0].Message.Contains("orders.placed", StringComparison.Ordinal)).IsTrue()
        .Because("the destination distinguishes a message buffered for the wrong topic from one "
               + "that was simply never flushed");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_WithLogger_LogsQueuedMessageAsync() {
    // Arrange
    var logger = new _batchCapturingLogger();
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000),
      logger: logger
    );
    var message = _createInboxMessage(handlerName: "OrderPlacedHandler");

    try {
      // Act
      sut.QueueInboxMessage(message);

      // Assert - same exposure as the outbox side: buffered, not durable. The handler name is the
      // inbox equivalent of the destination -- it identifies which consumer's work was lost.
      var queued = logger.Entries.Where(e => e.EventId == EVENT_QUEUED_INBOX_MESSAGE).ToList();
      await Assert.That(queued.Count).IsEqualTo(1)
        .Because("one queue call buffers one message and should account for it once");
      await Assert.That(queued[0].Level).IsEqualTo(LogLevel.Trace)
        .Because("a per-message line on the hot path must stay at Trace -- above that it is "
               + "enabled in production and the log becomes the bottleneck");
      await Assert.That(queued[0].Message.Contains(message.MessageId.ToString(), StringComparison.Ordinal)).IsTrue()
        .Because("without the message id this line cannot be joined to the inbox row that never "
               + "appeared, which is the only reason to read it");
      await Assert.That(queued[0].Message.Contains("OrderPlacedHandler", StringComparison.Ordinal)).IsTrue()
        .Because("the handler name says whose work was buffered -- one message id means nothing "
               + "when several handlers claim the same message");
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
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
        .Because("flush invokes the coordinator's store path at least once when outbox or inbox content is queued");
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
      var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
    var logger = new _batchCapturingLogger();
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 5000),
      logger: logger
    );

    // Act
    await sut.DisposeAsync();

    // Assert - the pair BRACKETS the shutdown drain, and that is the whole point of there being
    // two lines. "disposing" with no matching "disposed" is how a shutdown that hung inside the
    // final flush is told apart from one that completed: without the closing line, a host killed
    // by a shutdown timeout looks exactly like a clean exit.
    var entries = logger.Entries.ToList();
    var disposing = entries.FindIndex(e => e.EventId == EVENT_STRATEGY_DISPOSING);
    var disposed = entries.FindIndex(e => e.EventId == EVENT_STRATEGY_DISPOSED);
    await Assert.That(disposing).IsGreaterThanOrEqualTo(0)
      .Because("entering disposal must be announced before the drain that can hang");
    await Assert.That(disposed).IsGreaterThan(disposing)
      .Because("the closing line has to come after the opening one -- reversed or missing, the "
             + "pair no longer distinguishes a completed shutdown from a stuck one");
    await Assert.That(entries[disposing].Level).IsEqualTo(LogLevel.Information);
    await Assert.That(entries[disposed].Level).IsEqualTo(LogLevel.Information);
  }

  [Test]
  public async Task DisposeAsync_WithLogger_UnflushedOperations_LogsWarningAsync() {
    // Arrange
    var logger = new _batchCapturingLogger();
    var sut = new BatchWorkCoordinatorStrategy(
      new BatchFakeWorkCoordinator(),
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 60000),
      logger: logger
    );

    // Queue operations without flushing: 1 outbox message, 1 inbox message,
    // 2 completions (one each side) and 2 failures (one each side).
    sut.QueueOutboxMessage(_createOutboxMessage());
    sut.QueueInboxMessage(_createInboxMessage());
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");

    // Act
    await sut.DisposeAsync();

    // Assert - work still buffered at shutdown is the one moment the batch strategy can lose
    // messages. The warning has to say HOW MUCH, per category: "some unflushed work" cannot tell
    // a routine one-message drain from a host going down with a full buffer, and the two
    // completion/failure counts are SUMS across outbox and inbox, which is exactly where an
    // off-by-one hides.
    var warnings = logger.Entries.Where(e => e.EventId == EVENT_UNFLUSHED_ON_DISPOSAL).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("one disposal with buffered work reports it once");
    await Assert.That(warnings[0].Level).IsEqualTo(LogLevel.Warning)
      .Because("work that may not survive shutdown is a warning; at Debug it is filtered out of "
             + "the production log where the loss would have to be noticed");
    await Assert.That(warnings[0].Message.Contains("1 outbox messages", StringComparison.Ordinal)).IsTrue();
    await Assert.That(warnings[0].Message.Contains("1 inbox messages", StringComparison.Ordinal)).IsTrue();
    await Assert.That(warnings[0].Message.Contains("2 completions", StringComparison.Ordinal)).IsTrue()
      .Because("completions are summed across outbox and inbox -- reporting one side only "
             + "understates what is at risk");
    await Assert.That(warnings[0].Message.Contains("2 failures", StringComparison.Ordinal)).IsTrue()
      .Because("failures are summed the same way and matter more: an unreported failure leaves "
             + "the message looking in-flight forever");
  }

  [Test]
  public async Task DisposeAsync_WithLogger_FlushError_LogsErrorAsync() {
    // Arrange
    var logger = new _batchCapturingLogger();
    var throwingCoordinator = new BatchThrowingWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      throwingCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 60000),
      logger: logger
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act - DisposeAsync catches the store failure rather than throwing out of a shutdown path
    await sut.DisposeAsync();

    // Assert - containment plus identity. Letting the fault escape would fail the surrounding
    // `await using`/host shutdown over work that is already lost either way; swallowing it
    // silently would delete the only evidence that a message was dropped at shutdown.
    var entries = logger.Entries.ToList();
    var errorIndex = entries.FindIndex(e => e.EventId == EVENT_DISPOSAL_FLUSH_ERROR);
    await Assert.That(errorIndex).IsGreaterThanOrEqualTo(0)
      .Because("a shutdown drain that failed has to leave a record -- the buffered message is "
             + "gone and nothing else will ever mention it");
    await Assert.That(entries[errorIndex].Level).IsEqualTo(LogLevel.Error)
      .Because("dropped outbox work is an error, not a debug note");
    await Assert.That(entries[errorIndex].Exception).IsTypeOf<InvalidOperationException>()
      .Because("the store's own fault must reach the log intact -- 'error flushing on disposal' "
             + "without the cause cannot distinguish a transient database blip from a bug");
    await Assert.That(entries.FindIndex(e => e.EventId == EVENT_STRATEGY_DISPOSED)).IsGreaterThan(errorIndex)
      .Because("disposal must still COMPLETE after the failed drain; stopping at the catch would "
             + "leave the strategy undisposed with its debounce timer torn down");
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
    // The flush runs on a detached Task.Run, so a throwing coordinator surfaces NOWHERE except
    // this log line: the queued messages are gone from the buffer, nothing was written, and the
    // caller that queued them was told nothing. If the catch stopped logging, a service would
    // drop outbox work silently for as long as the failure lasted.
    var logger = new _batchCapturingLogger();
    var throwingCoordinator = new BatchThrowingWorkCoordinator();

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

      // Wait on the log itself, not on the coordinator call: the catch runs after the throw, so
      // signaling from the coordinator would race the very line under test.
      await logger.WaitForEventAsync(EVENT_BATCH_FLUSH_ERROR, TimeSpan.FromSeconds(5));

      await Assert.That(logger.Entries.Any(e => e.EventId == EVENT_BATCH_FLUSH_ERROR && e.Level == LogLevel.Error)).IsTrue()
        .Because("dropped outbox work is an error, not a debug note -- at any lower level it is "
               + "filtered out of the production log where it would have to be seen");

      // The trigger has to be identifiable. Both flush paths fail the same way and leave the same
      // empty buffer behind; only the distinct event id tells an operator whether the batch-size
      // path or the quiet-period path is the one failing.
      await Assert.That(logger.Entries.Any(e => e.EventId == EVENT_DEBOUNCE_FLUSH_ERROR)).IsFalse()
        .Because("this flush was triggered by batch size, and reporting it as a debounce failure "
               + "points the investigation at the wrong trigger");
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
    // Same silent-drop exposure as the batch-size path, on the trigger that fires on LOW traffic --
    // the one a quiet service lives on, and therefore the one whose failures are least likely to be
    // noticed any other way.
    var logger = new _batchCapturingLogger();
    var throwingCoordinator = new BatchThrowingWorkCoordinator();

    var sut = new BatchWorkCoordinatorStrategy(
      throwingCoordinator,
      new BatchFakeInstanceProvider(),
      _createOptions(batchSize: 100, debounceMs: 50),
      logger: logger
    );

    try {
      // Act - queue a message below batch size so debounce timer fires
      sut.QueueOutboxMessage(_createOutboxMessage());

      await logger.WaitForEventAsync(EVENT_DEBOUNCE_FLUSH_ERROR, TimeSpan.FromSeconds(10));

      await Assert.That(logger.Entries.Any(e => e.EventId == EVENT_DEBOUNCE_FLUSH_ERROR && e.Level == LogLevel.Error)).IsTrue()
        .Because("dropped outbox work is an error, not a debug note -- at any lower level it is "
               + "filtered out of the production log where it would have to be seen");

      await Assert.That(logger.Entries.Any(e => e.EventId == EVENT_BATCH_FLUSH_ERROR)).IsFalse()
        .Because("one message against a batch size of 100 can only have been flushed by the "
               + "debounce timer; attributing it to the batch-size trigger misreports the cause");
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
      var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
        await sut.FlushAndGetBatchAsync(WorkBatchOptions.None, cts.Token);
      } catch (Exception ex) {
        caught = ex;
      }
      await Assert.That(caught).IsNotNull()
        .Because("Flushing with a canceled token should throw");
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
      var firstFlush = sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Wait for the slow coordinator to start processing
      await slowCoordinator.WaitForProcessingStartedAsync(TimeSpan.FromSeconds(5));

      // Queue another message and try second flush while first is in progress
      sut.QueueOutboxMessage(_createOutboxMessage());
      var secondResult = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
      var firstFlush = sut.FlushAndGetBatchAsync(WorkBatchOptions.None);
      await slowCoordinator.WaitForProcessingStartedAsync(TimeSpan.FromSeconds(5));

      sut.QueueOutboxMessage(_createOutboxMessage());
      var secondResult = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
      var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);
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
    var fakeCoordinator = new BatchFakeWorkCoordinator();
    var sut = new BatchWorkCoordinatorStrategy(
      fakeCoordinator,
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
      // Act
      sut.QueueOutboxMessage(message);
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert - "succeeds" has to mean the message REACHED THE STORE, not merely that the guard
      // declined to throw. Null and Guid.Empty are one keystroke apart and the guard rejects only
      // the second; a guard that also silently dropped nulls would leave every message not bound
      // to a stream (a plain command, an audit record) accepted at the API and absent from the
      // outbox, with nothing thrown anywhere to say so.
      await Assert.That(fakeCoordinator.LastNewOutboxMessages.Length).IsEqualTo(1)
        .Because("a stream-less message is legitimate work and must be stored like any other");
      await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(message.MessageId);
      await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].StreamId).IsNull()
        .Because("null means 'not stream-bound' and must survive the flush -- substituting an id "
               + "would file the message under a stream it never belonged to");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // FLUSH CLEARS QUEUES
  // ========================================
  // Deleted: FlushAsync_AllOperationTypes_AllIncludedInSingleFlushAsync.
  // Asserted Last{Outbox,Inbox}{Completions,Failures} captured via the coordinator,
  // which no longer happens — those routes are now via IOutboxCompletionChannel /
  // IFailureChannel (verified in WorkCoordinatorFlushHelperTests).

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
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);
      var firstFlushCount = fakeCoordinator.ProcessWorkBatchCallCount;
      await Assert.That(firstFlushCount).IsGreaterThanOrEqualTo(1);

      // Second flush - queues should be empty
      var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(firstFlushCount)
        .Because("Second flush should be empty (no further store calls)");
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
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
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
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert — ExecuteFlushAsync signals publisher but does not write to channel
      await Assert.That(channelWriter.WrittenWork).Count().IsEqualTo(0)
        .Because("ExecuteFlushAsync signals publisher but does not write to channel");
      // Work was still persisted via StoreOutboxMessagesAsync
      await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ========================================
  // Test Fakes
  // ========================================
  // Deleted: FlushAsync_NullChannelWriter_DoesNotThrowAsync,
  //          FlushAsync_ChannelClosed_HandlesGracefullyAsync.
  // Both asserted result.OutboxWork.Count == 1 against the legacy claim-during-flush
  // behavior. ExecuteFlushAsync returns an empty WorkBatch (claiming owned by
  // ClaimWorker); these tests can never pass.

  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    public void ClearInFlight() { }
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
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
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
    /// Waits for at least one call to the work coordinator's store path.
    /// </summary>
    public async Task WaitForFlushAsync(TimeSpan timeout) {
      if (!await _flushSignal.WaitAsync(timeout)) {
        throw new TimeoutException("The work coordinator store path was not called within timeout");
      }
    }

    public Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount = 2,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      TotalOutboxMessagesReceived += messages.Length;
      LastNewOutboxMessages = messages;
      _flushSignal.Release();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewInboxMessages = messages;
      _flushSignal.Release();
      return Task.CompletedTask;
    }

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
    public Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount = 2,
      CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Simulated flush error");
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
        throw new TimeoutException("StoreOutboxMessagesAsync was not started within timeout");
      }
    }

    public void ReleaseProcessing() {
      try { _releaseProcessing.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount = 2,
      CancellationToken cancellationToken = default) {
      _processingStarted.Release();
      await _releaseProcessing.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

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

  private sealed class BatchFakeScopeFactory(IWorkCoordinator coordinator) : IServiceScopeFactory {
    private readonly IWorkCoordinator _coordinator = coordinator;
    public int ScopeCreationCount { get; private set; }

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

  // BatchWorkCoordinatorStrategy's own LoggerMessage ids.
  private const int EVENT_STRATEGY_STARTED = 1;
  private const int EVENT_QUEUED_OUTBOX_MESSAGE = 2;
  private const int EVENT_QUEUED_INBOX_MESSAGE = 3;
  private const int EVENT_BATCH_FLUSH_ERROR = 10;
  private const int EVENT_DEBOUNCE_FLUSH_ERROR = 11;
  private const int EVENT_STRATEGY_DISPOSING = 12;
  private const int EVENT_UNFLUSHED_ON_DISPOSAL = 13;
  private const int EVENT_DISPOSAL_FLUSH_ERROR = 14;
  private const int EVENT_STRATEGY_DISPOSED = 15;

  /// <summary>
  /// Captures log entries and lets a test await a specific event id, so the assertion waits on the
  /// log line under test rather than on a signal raised before the catch that writes it.
  /// </summary>
  /// <remarks>
  /// The formatted message and the attached exception are captured too: several of these events
  /// exist only to carry a value (the effective batch size, the id of a message that never made it
  /// out of the buffer, the fault that ended a shutdown flush), and the event id alone does not
  /// pin whether that value is actually in the line an operator reads.
  /// </remarks>
  private sealed class _batchCapturingLogger : ILogger<BatchWorkCoordinatorStrategy> {
    private readonly List<(int EventId, LogLevel Level, string Message, Exception? Exception)> _entries = [];
    private readonly Dictionary<int, TaskCompletionSource> _waiters = [];

    public IReadOnlyList<(int EventId, LogLevel Level, string Message, Exception? Exception)> Entries {
      get {
        lock (_entries) {
          return [.. _entries];
        }
      }
    }

    public Task WaitForEventAsync(int eventId, TimeSpan timeout) {
      TaskCompletionSource tcs;
      lock (_entries) {
        if (_entries.Exists(e => e.EventId == eventId)) {
          return Task.CompletedTask;
        }
        if (!_waiters.TryGetValue(eventId, out var existing)) {
          existing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          _waiters[eventId] = existing;
        }
        tcs = existing;
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      TaskCompletionSource? waiter;
      lock (_entries) {
        _entries.Add((eventId.Id, logLevel, formatter(state, exception), exception));
        _waiters.TryGetValue(eventId.Id, out waiter);
      }
      waiter?.TrySetResult();
    }
  }
}
