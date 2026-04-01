using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="SlidingWindowInboxBatchStrategy"/> — the default inbox batch strategy
/// that collects messages and flushes via a single <c>process_work_batch</c> call.
/// <para>
/// Tests verify three flush triggers (batch size, sliding window, hard max) and
/// correct WorkBatch distribution back to waiting handlers.
/// </para>
/// </summary>
/// <tests>src/Whizbang.Core/Workers/SlidingWindowInboxBatchStrategy.cs</tests>
/// <docs>messaging/transports/transport-consumer#inbox-batching</docs>
public class SlidingWindowInboxBatchStrategyTests {

  // ========================================
  // HELPERS
  // ========================================

  private static InboxMessage _createInboxMessage(Guid? messageId = null) {
    var id = messageId ?? Guid.CreateVersion7();
    return new InboxMessage {
      MessageId = id,
      HandlerName = "TestHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      EnvelopeType = "TestEnvelopeType",
      StreamId = Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly"
    };
  }

  private static MessageProcessingOptions _createOptions(
    int batchSize = 5,
    int slideMs = 50,
    int maxWaitMs = 1000
  ) => new() {
    InboxBatchSize = batchSize,
    InboxBatchSlideMs = slideMs,
    InboxBatchMaxWaitMs = maxWaitMs,
    MaxConcurrentMessages = 40
  };

  /// <summary>
  /// Creates a service provider with a fake <see cref="IWorkCoordinatorStrategy"/> that
  /// tracks queued inbox messages and returns them as <see cref="InboxWork"/> in the <see cref="WorkBatch"/>.
  /// </summary>
  private static (IServiceScopeFactory ScopeFactory, InboxBatchFakeWorkCoordinatorStrategy Strategy) _createScopeFactory() {
    var strategy = new InboxBatchFakeWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    var sp = services.BuildServiceProvider();
    return (sp.GetRequiredService<IServiceScopeFactory>(), strategy);
  }

  // ========================================
  // BATCH SIZE TRIGGER TESTS
  // ========================================

  [Test]
  public async Task EnqueueAndWaitAsync_FlushesWhenBatchSizeReachedAsync() {
    // Arrange
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    var options = _createOptions(batchSize: 3, slideMs: 5000, maxWaitMs: 10000);

    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    // Act — enqueue exactly batchSize messages
    var msg1 = _createInboxMessage();
    var msg2 = _createInboxMessage();
    var msg3 = _createInboxMessage();

    var task1 = sut.EnqueueAndWaitAsync(msg1, CancellationToken.None);
    var task2 = sut.EnqueueAndWaitAsync(msg2, CancellationToken.None);
    var task3 = sut.EnqueueAndWaitAsync(msg3, CancellationToken.None);

    // All three should complete because batch size was reached
    var results = await Task.WhenAll(task1, task2, task3);

    // Assert — single flush with all 3 messages
    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(1);
    await Assert.That(fakeStrategy.QueuedInboxMessages).Count().IsEqualTo(3);
    await Assert.That(results[0]).IsNotNull();
    await Assert.That(results[1]).IsNotNull();
    await Assert.That(results[2]).IsNotNull();
  }

  [Test]
  public async Task EnqueueAndWaitAsync_DoesNotFlushBelowBatchSizeAsync() {
    // Arrange
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    var options = _createOptions(batchSize: 10, slideMs: 5000, maxWaitMs: 10000);

    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    // Act — enqueue fewer than batchSize messages
    var msg1 = _createInboxMessage();
    var task1 = sut.EnqueueAndWaitAsync(msg1, CancellationToken.None);

    // Wait briefly — should NOT have flushed yet
    await Task.Delay(100);

    // Assert — no flush has occurred
    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(0);
    await Assert.That(task1.IsCompleted).IsFalse();
  }

  // ========================================
  // SLIDING WINDOW TIMER TESTS
  // ========================================

  [Test]
  public async Task EnqueueAndWaitAsync_FlushesAfterSlideWindowExpiresAsync() {
    // Arrange
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    var options = _createOptions(batchSize: 100, slideMs: 50, maxWaitMs: 10000);

    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    // Act — enqueue one message, then wait for slide window to expire
    var msg = _createInboxMessage();
    var task = sut.EnqueueAndWaitAsync(msg, CancellationToken.None);

    // Wait longer than slideMs
    var result = await task.WaitAsync(TimeSpan.FromSeconds(2));

    // Assert — flush triggered by slide window
    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(1);
    await Assert.That(fakeStrategy.QueuedInboxMessages).Count().IsEqualTo(1);
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task EnqueueAndWaitAsync_SlideWindowResetsOnNewMessageAsync() {
    // Arrange
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    var options = _createOptions(batchSize: 100, slideMs: 100, maxWaitMs: 10000);

    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    // Act — enqueue first message, wait 60ms (< 100ms), then enqueue second
    var msg1 = _createInboxMessage();
    var task1 = sut.EnqueueAndWaitAsync(msg1, CancellationToken.None);

    await Task.Delay(60);

    var msg2 = _createInboxMessage();
    var task2 = sut.EnqueueAndWaitAsync(msg2, CancellationToken.None);

    // Slide window should reset — both messages should be in same batch
    var results = await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(2));

    // Assert — single flush with both messages
    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(1);
    await Assert.That(fakeStrategy.QueuedInboxMessages).Count().IsEqualTo(2);
  }

  // ========================================
  // HARD MAX TIMER TESTS
  // ========================================

  [Test]
  public async Task EnqueueAndWaitAsync_FlushesAtHardMaxEvenIfMessagesKeepArrivingAsync() {
    // Arrange
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    // slideMs = very long (won't trigger), hardMax = 200ms, batchSize = very large
    var options = _createOptions(batchSize: 1000, slideMs: 5000, maxWaitMs: 200);

    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    // Act — enqueue a message, then keep enqueueing within slide window
    var msg1 = _createInboxMessage();
    var task1 = sut.EnqueueAndWaitAsync(msg1, CancellationToken.None);

    // Keep adding messages every 50ms (within slide window) — but hard max should fire at 200ms
    await Task.Delay(80);
    var msg2 = _createInboxMessage();
    var task2 = sut.EnqueueAndWaitAsync(msg2, CancellationToken.None);

    await Task.Delay(80);
    var msg3 = _createInboxMessage();
    var task3 = sut.EnqueueAndWaitAsync(msg3, CancellationToken.None);

    // Wait for hard max to fire
    var results = await Task.WhenAll(task1, task2, task3).WaitAsync(TimeSpan.FromSeconds(2));

    // Assert — flushed by hard max timer
    await Assert.That(fakeStrategy.FlushCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(fakeStrategy.QueuedInboxMessages.Count).IsGreaterThanOrEqualTo(3);
  }

  // ========================================
  // CANCELLATION TESTS
  // ========================================

  [Test]
  public async Task EnqueueAndWaitAsync_CancellationCancelsWaitingTaskAsync() {
    // Arrange
    var (scopeFactory, _) = _createScopeFactory();
    var options = _createOptions(batchSize: 100, slideMs: 5000, maxWaitMs: 10000);

    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);
    using var cts = new CancellationTokenSource();

    // Act
    var msg = _createInboxMessage();
    var task = sut.EnqueueAndWaitAsync(msg, cts.Token);

    // Cancel before any flush trigger
    await cts.CancelAsync();

    // Assert — task should be cancelled
    await Assert.That(async () => await task).ThrowsExactly<TaskCanceledException>();
  }

  // ========================================
  // DISPOSE TESTS
  // ========================================

  [Test]
  public async Task DisposeAsync_FlushesPendingMessagesAsync() {
    // Arrange
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    var options = _createOptions(batchSize: 100, slideMs: 5000, maxWaitMs: 10000);

    var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    var msg = _createInboxMessage();
    var task = sut.EnqueueAndWaitAsync(msg, CancellationToken.None);

    // Act — dispose should flush remaining messages
    await sut.DisposeAsync();

    // Assert
    var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(1);
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task DisposeAsync_IsIdempotentAsync() {
    // Arrange
    var (scopeFactory, _) = _createScopeFactory();
    var options = _createOptions();

    var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    // Act — dispose twice should not throw
    await sut.DisposeAsync();
    await sut.DisposeAsync();
  }

  // ========================================
  // WORKBATCH DISTRIBUTION TESTS
  // ========================================

  [Test]
  public async Task EnqueueAndWaitAsync_AllHandlersReceiveSameWorkBatchAsync() {
    // Arrange
    var (scopeFactory, _) = _createScopeFactory();
    var options = _createOptions(batchSize: 3, slideMs: 5000, maxWaitMs: 10000);

    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    // Act
    var task1 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    var task2 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    var task3 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);

    var results = await Task.WhenAll(task1, task2, task3);

    // Assert — all handlers receive the exact same WorkBatch instance
    await Assert.That(ReferenceEquals(results[0], results[1])).IsTrue();
    await Assert.That(ReferenceEquals(results[1], results[2])).IsTrue();
  }

  [Test]
  public async Task EnqueueAndWaitAsync_FlushErrorPropagatedToAllWaitersAsync() {
    // Arrange — use a strategy that throws on flush
    var throwingStrategy = new InboxBatchThrowingWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => throwingStrategy);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var options = _createOptions(batchSize: 2, slideMs: 5000, maxWaitMs: 10000);

    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    // Act
    var task1 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    var task2 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);

    // Assert — both tasks should fail with the same exception
    await Assert.That(async () => await task1).ThrowsExactly<InvalidOperationException>();
    await Assert.That(async () => await task2).ThrowsExactly<InvalidOperationException>();
  }

  // ========================================
  // MULTIPLE BATCH TESTS
  // ========================================

  [Test]
  public async Task EnqueueAndWaitAsync_MultipleBatchesProcessedSequentiallyAsync() {
    // Arrange
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    var options = _createOptions(batchSize: 2, slideMs: 5000, maxWaitMs: 10000);

    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    // Act — first batch of 2
    var batch1Task1 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    var batch1Task2 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);

    await Task.WhenAll(batch1Task1, batch1Task2);

    // Second batch of 2
    var batch2Task1 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    var batch2Task2 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);

    await Task.WhenAll(batch2Task1, batch2Task2);

    // Assert — two separate flushes
    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(2);
  }

  // ========================================
  // CONSTRUCTOR NULL CHECK TESTS
  // ========================================

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    var (scopeFactory, _) = _createScopeFactory();
    await Assert.That(() => new SlidingWindowInboxBatchStrategy(null!, scopeFactory))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    var options = _createOptions();
    await Assert.That(() => new SlidingWindowInboxBatchStrategy(options, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // DISPOSED STATE TESTS
  // ========================================

  [Test]
  public async Task EnqueueAndWaitAsync_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    var (scopeFactory, _) = _createScopeFactory();
    var sut = new SlidingWindowInboxBatchStrategy(_createOptions(), scopeFactory);
    await sut.DisposeAsync();

    await Assert.That(async () => await sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None))
      .ThrowsExactly<ObjectDisposedException>();
  }

  // ========================================
  // EDGE CASE TESTS
  // ========================================

  [Test]
  public async Task EnqueueAndWaitAsync_BatchSizePlusOne_OverflowsToNextBatchAsync() {
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    var options = _createOptions(batchSize: 2, slideMs: 50, maxWaitMs: 10000);
    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory);

    var task1 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    var task2 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    await Task.WhenAll(task1, task2);

    var task3 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    await task3.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(2);
  }

  [Test]
  public async Task EnqueueAndWaitAsync_WithNullMetrics_DoesNotCrashAsync() {
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    var options = _createOptions(batchSize: 2, slideMs: 5000, maxWaitMs: 10000);
    await using var sut = new SlidingWindowInboxBatchStrategy(options, scopeFactory, metrics: null);

    var task1 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    var task2 = sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    await Task.WhenAll(task1, task2);

    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(1);
  }
}

// ========================================
// TEST DOUBLES
// ========================================

/// <summary>
/// Fake <see cref="IWorkCoordinatorStrategy"/> that records calls and returns work batches
/// with inbox work items matching queued messages.
/// </summary>
internal sealed class InboxBatchFakeWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
  private readonly List<InboxMessage> _queuedInbox = [];
  private int _flushCallCount;

  public IReadOnlyList<InboxMessage> QueuedInboxMessages => _queuedInbox;
  public int FlushCallCount => _flushCallCount;

  public void QueueInboxMessage(InboxMessage message) {
    _queuedInbox.Add(message);
  }

  public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
    Interlocked.Increment(ref _flushCallCount);

    // Return inbox work items for each queued message
    var inboxWork = _queuedInbox.Select(msg => new InboxWork {
      MessageId = msg.MessageId,
      Envelope = msg.Envelope,
      MessageType = msg.MessageType,
      StreamId = msg.StreamId,
      Flags = WorkBatchOptions.None
    }).ToList();

    return Task.FromResult(new WorkBatch {
      InboxWork = inboxWork,
      OutboxWork = [],
      PerspectiveWork = []
    });
  }

  public void QueueOutboxMessage(OutboxMessage message) { }
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
}

/// <summary>
/// Work coordinator strategy that throws on flush — for testing error propagation.
/// </summary>
internal sealed class InboxBatchThrowingWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
  public void QueueInboxMessage(InboxMessage message) { }

  public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
    throw new InvalidOperationException("Simulated flush failure");
  }

  public void QueueOutboxMessage(OutboxMessage message) { }
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
}
