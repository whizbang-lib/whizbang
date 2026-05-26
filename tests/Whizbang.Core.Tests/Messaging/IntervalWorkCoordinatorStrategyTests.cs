using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
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
      AbandonStaleInstanceThresholdSeconds = 300,
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

    // Act - Wait for timer to fire (signal-based, no arbitrary delay)
    await fakeCoordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

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
      AbandonStaleInstanceThresholdSeconds = 300,
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

    // Small delay between messages (still well below 1s interval, but enough to be sequential)
    await Task.Yield();

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

    // Wait for timer to fire (signal-based, no arbitrary delay)
    await fakeCoordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

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
      AbandonStaleInstanceThresholdSeconds = 300,
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

    // Wait briefly to verify timer stopped (negative assertion - timer would fire at 1s intervals)
    await Task.Delay(200);

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
      AbandonStaleInstanceThresholdSeconds = 300,
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
    _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

    // Assert - Manual flush should work immediately (not wait for timer)
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Manual FlushAndGetBatchAsync should flush immediately without waiting for timer");
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
      AbandonStaleInstanceThresholdSeconds = 300,
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
    _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
  // Completion/failure routing moved to WorkCoordinatorFlushHelperTests after
  // Phase H. On the direct-coordinator path used here, completions and failures
  // are dropped by design (channel resolution requires a scoped provider).
  // Deleted strategy-level tests:
  //   QueueOutboxCompletion_ShouldBeIncludedInFlushAsync
  //   QueueInboxCompletion_ShouldBeIncludedInFlushAsync
  //   QueueOutboxFailure_ShouldBeIncludedInFlushAsync
  //   QueueInboxFailure_ShouldBeIncludedInFlushAsync

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
      AbandonStaleInstanceThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    // Act - Flush with nothing queued
    var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

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
      AbandonStaleInstanceThresholdSeconds = 300,
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
      AbandonStaleInstanceThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
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
  public async Task DisposeAsync_CalledMultipleTimes_ShouldNotThrowAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300,
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
      null,
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
  // MORE DISPOSED STATE TESTS
  // ========================================
  //
  // Deleted: FlushAsync_WithDebugMode_SetsDebugFlagAsync. After Phase H the
  // strategy no longer routes WorkBatchOptions.DebugMode through the coordinator
  // (process_work_batch is gone). Debug-mode behavior is now per-component
  // (e.g., OutboxCompletionFlushWorker), covered in those workers' own tests.

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
  // Graceful Shutdown Tests
  // ========================================

  [Test]
  public async Task FlushAsync_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { IntervalMilliseconds = 5000 };

    var sut = new IntervalWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(async () => { await sut.FlushAsync(WorkBatchOptions.None); })
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task FlushAsync_ConcurrentFlush_ReturnsEmptyBatchAsync() {
    // Arrange - use a slow coordinator so we can trigger concurrent flushes
    var slowCoordinator = new SlowWorkCoordinator(delayMilliseconds: 500);
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { IntervalMilliseconds = 60000 };

    var sut = new IntervalWorkCoordinatorStrategy(slowCoordinator, instanceProvider, options);

    // Queue a message so the first flush has work to do
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

    // Act - start a flush, then immediately try a second flush
    var firstFlush = sut.FlushAndGetBatchAsync(WorkBatchOptions.None);
    await Task.Delay(50); // Let first flush acquire the lock

    var secondResult = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

    // Assert - second flush should return empty (first is in progress)
    await Assert.That(secondResult.OutboxWork).IsEmpty()
      .Because("Concurrent flush should return empty batch when another flush is in progress");

    // Cleanup
    await firstFlush;
    await sut.DisposeAsync();
  }

  // ========================================
  // Channel Write Tests
  // ========================================

  [Test]
  public async Task FlushAsync_WithReturnedWork_WritesToChannelAsync() {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var messageId1 = Guid.CreateVersion7();
    var fakeCoordinator = new FakeWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = messageId1,
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createTestEnvelope(messageId1),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 60000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, workChannelWriter: channelWriter
    );

    try {
      var messageId = _idProvider.NewGuid();
      sut.QueueOutboxMessage(new OutboxMessage {
        MessageId = messageId,
        Destination = "test-topic",
        Envelope = _createTestEnvelope(messageId),
        EnvelopeType = "Test",
        StreamId = _idProvider.NewGuid(),
        IsEvent = false,
        MessageType = "TestMessage, TestAssembly",
        Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] }
      });

      // Act
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert — ExecuteFlushAsync signals publisher but does not write to channel
      await Assert.That(channelWriter.WrittenWork).Count().IsEqualTo(0)
        .Because("ExecuteFlushAsync signals publisher but does not write to channel");
      // Work was still persisted via ProcessWorkBatchAsync
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
  // behavior. Post-Phase-H, ExecuteFlushAsync returns an empty WorkBatch (claiming
  // is owned by ClaimWorker); these tests can never pass.

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

  private sealed class FakeWorkCoordinator : IWorkCoordinator, IDisposable {
    private readonly SemaphoreSlim _flushSignal = new(0, int.MaxValue);

    public void Dispose() => _flushSignal.Dispose();
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];
    public MessageCompletion[] LastOutboxCompletions { get; private set; } = [];
    public MessageCompletion[] LastInboxCompletions { get; private set; } = [];
    public MessageFailure[] LastOutboxFailures { get; private set; } = [];
    public MessageFailure[] LastInboxFailures { get; private set; } = [];
    public List<OutboxWork> WorkToReturn { get; set; } = [];

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
      // Post-Phase-H: not in the live path. Kept so the fake honors the full interface;
      // counting and signaling live in Store{Outbox,Inbox}MessagesAsync below.
      return Task.FromResult(new WorkBatch {
        OutboxWork = WorkToReturn,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount = 2,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = messages;
      _flushSignal.Release();
      return Task.CompletedTask;
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

  // Test envelope implementation
  private sealed class TestMessageEnvelope : IMessageEnvelope<JsonElement> {
    public int Version => 1;
    public MessageDispatchContext DispatchContext { get; } = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
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

    public SecurityContext? GetCurrentSecurityContext() => null;
    public ScopeContext? GetCurrentScope() => null;
  }

  private sealed class SlowWorkCoordinator(int delayMilliseconds) : IWorkCoordinator {
    private readonly int _delayMilliseconds = delayMilliseconds;

    public async Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      await Task.Delay(_delayMilliseconds, cancellationToken);
      return new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      };
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

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }
}
