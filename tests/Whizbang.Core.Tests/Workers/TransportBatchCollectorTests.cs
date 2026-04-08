using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="TransportBatchCollector{T}"/> — the transport-level batch collector
/// that buffers messages and flushes them via a callback when a batch is ready.
/// <para>
/// Three flush triggers (whichever fires first):
/// <list type="bullet">
///   <item>Batch size: <c>BatchSize</c> messages accumulated → immediate flush</item>
///   <item>Sliding window: <c>SlideMs</c> ms since last enqueue → flush partial batch</item>
///   <item>Hard max: <c>MaxWaitMs</c> ms since first message in batch → flush regardless</item>
/// </list>
/// </para>
/// </summary>
[Category("Workers")]
public class TransportBatchCollectorTests {

  // ========================================
  // Batch Size Trigger
  // ========================================

  [Test]
  public async Task Enqueue_FlushesImmediatelyWhenBatchSizeReachedAsync() {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushSignal = new TaskCompletionSource();
    var options = new TransportBatchOptions { BatchSize = 3, SlideMs = 5000, MaxWaitMs = 10000 };

    await using var collector = new TransportBatchCollector<int>(options, async batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      await Task.CompletedTask;
    });

    // Act — enqueue exactly batchSize items
    collector.Enqueue(1);
    collector.Enqueue(2);
    collector.Enqueue(3);

    await flushSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // Assert — single flush with all 3 items
    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(3);
    await Assert.That(flushedBatches[0][0]).IsEqualTo(1);
    await Assert.That(flushedBatches[0][1]).IsEqualTo(2);
    await Assert.That(flushedBatches[0][2]).IsEqualTo(3);
  }

  [Test]
  public async Task Enqueue_DoesNotFlushBelowBatchSizeAsync() {
    // Arrange
    var flushCount = 0;
    var options = new TransportBatchOptions { BatchSize = 10, SlideMs = 5000, MaxWaitMs = 10000 };

    await using var collector = new TransportBatchCollector<int>(options, async batch => {
      Interlocked.Increment(ref flushCount);
      await Task.CompletedTask;
    });

    // Act — enqueue fewer than batchSize
    collector.Enqueue(1);
    collector.Enqueue(2);

    await Task.Delay(100);

    // Assert — no flush yet (well below batch size and timers are long)
    await Assert.That(flushCount).IsEqualTo(0);
  }

  // ========================================
  // Sliding Window Trigger
  // ========================================

  [Test]
  public async Task Enqueue_FlushesOnSlidingWindowTimeoutAsync() {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushSignal = new TaskCompletionSource();
    var options = new TransportBatchOptions { BatchSize = 1000, SlideMs = 50, MaxWaitMs = 10000 };

    await using var collector = new TransportBatchCollector<int>(options, async batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      await Task.CompletedTask;
    });

    // Act — enqueue a few items, then stop (let sliding window fire)
    collector.Enqueue(1);
    collector.Enqueue(2);

    await flushSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // Assert — flushed after sliding window with the 2 items
    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Enqueue_SlidingWindowResetsOnNewMessageAsync() {
    // Arrange
    var flushCount = 0;
    var options = new TransportBatchOptions { BatchSize = 1000, SlideMs = 100, MaxWaitMs = 10000 };

    await using var collector = new TransportBatchCollector<int>(options, async batch => {
      Interlocked.Increment(ref flushCount);
      await Task.CompletedTask;
    });

    // Act — enqueue with delays shorter than slide window (keeps resetting)
    collector.Enqueue(1);
    await Task.Delay(50);  // 50ms < 100ms slide
    collector.Enqueue(2);
    await Task.Delay(50);  // another 50ms < 100ms slide
    collector.Enqueue(3);

    // At this point, no flush should have fired (slide keeps resetting)
    await Assert.That(flushCount).IsEqualTo(0)
      .Because("Sliding window should reset on each new message");
  }

  // ========================================
  // Hard Max Trigger
  // ========================================

  [Test]
  public async Task Enqueue_FlushesOnHardMaxTimeoutAsync() {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushSignal = new TaskCompletionSource();
    var options = new TransportBatchOptions { BatchSize = 1000, SlideMs = 5000, MaxWaitMs = 100 };

    await using var collector = new TransportBatchCollector<int>(options, async batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      await Task.CompletedTask;
    });

    // Act — enqueue one item, hard max fires at 100ms regardless of slide
    collector.Enqueue(1);

    await flushSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // Assert — flushed after hard max
    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(1);
  }

  // ========================================
  // Flush Callback Receives All Messages
  // ========================================

  [Test]
  public async Task FlushCallback_ReceivesAllBatchedMessagesInOrderAsync() {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<string>>();
    var flushSignal = new TaskCompletionSource();
    var options = new TransportBatchOptions { BatchSize = 5, SlideMs = 5000, MaxWaitMs = 10000 };

    await using var collector = new TransportBatchCollector<string>(options, async batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      await Task.CompletedTask;
    });

    // Act
    collector.Enqueue("alpha");
    collector.Enqueue("bravo");
    collector.Enqueue("charlie");
    collector.Enqueue("delta");
    collector.Enqueue("echo");

    await flushSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // Assert — all 5 in order
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(5);
    await Assert.That(flushedBatches[0][0]).IsEqualTo("alpha");
    await Assert.That(flushedBatches[0][4]).IsEqualTo("echo");
  }

  // ========================================
  // Dispose Flushes Remaining
  // ========================================

  [Test]
  public async Task DisposeAsync_FlushesRemainingMessagesAsync() {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var options = new TransportBatchOptions { BatchSize = 1000, SlideMs = 5000, MaxWaitMs = 10000 };

    var collector = new TransportBatchCollector<int>(options, async batch => {
      flushedBatches.Add(batch);
      await Task.CompletedTask;
    });

    // Act — enqueue items but don't trigger any flush
    collector.Enqueue(1);
    collector.Enqueue(2);
    collector.Enqueue(3);

    // Dispose should flush remaining
    await collector.DisposeAsync();

    // Assert
    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(3);
  }

  // ========================================
  // Multiple Batches
  // ========================================

  [Test]
  public async Task Enqueue_MultipleBatchesFlushIndependentlyAsync() {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushCount = 0;
    var secondFlush = new TaskCompletionSource();
    var options = new TransportBatchOptions { BatchSize = 3, SlideMs = 5000, MaxWaitMs = 10000 };

    await using var collector = new TransportBatchCollector<int>(options, async batch => {
      flushedBatches.Add(batch);
      if (Interlocked.Increment(ref flushCount) == 2) {
        secondFlush.TrySetResult();
      }
      await Task.CompletedTask;
    });

    // Act — enqueue 6 items → should trigger 2 batches of 3
    collector.Enqueue(1);
    collector.Enqueue(2);
    collector.Enqueue(3); // flush 1 triggers

    // Wait for first flush to complete before enqueueing second batch
    // (flush runs on Task.Run, need to let it swap the pending list)
    await Task.Delay(50);

    collector.Enqueue(4);
    collector.Enqueue(5);
    collector.Enqueue(6); // flush 2 triggers

    await secondFlush.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // Assert — total messages across both batches is 6
    await Assert.That(flushedBatches).Count().IsEqualTo(2);
    var totalMessages = flushedBatches[0].Count + flushedBatches[1].Count;
    await Assert.That(totalMessages).IsEqualTo(6);
  }
}
