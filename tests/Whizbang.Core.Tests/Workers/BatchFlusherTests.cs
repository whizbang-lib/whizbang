using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

[NotInParallel(Order = 100)]
public class BatchFlusherTests {

  [Test]
  public async Task SingleItem_FlushesAfterCoalesceWindowAsync() {
    var batchTcs = new TaskCompletionSource<IReadOnlyList<int>>();
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        batchTcs.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 25,
        MaxBatchSize = 100,
        ImmediateFlushThreshold = 50
      },
      logger: NullLogger.Instance);

    await flusher.Writer.WriteAsync(42);

    var batch = await batchTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(batch.Count).IsEqualTo(1);
    await Assert.That(batch[0]).IsEqualTo(42);
  }

  [Test]
  public async Task ImmediateThresholdReached_FlushesEarlyAsync() {
    var batchTcs = new TaskCompletionSource<IReadOnlyList<int>>();
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        batchTcs.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 5_000,             // Long window — should not be reached
        MaxBatchSize = 100,
        ImmediateFlushThreshold = 5
      },
      logger: NullLogger.Instance);

    for (var i = 0; i < 10; i++) {
      await flusher.Writer.WriteAsync(i);
    }

    var batch = await batchTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(batch.Count).IsGreaterThanOrEqualTo(5);
    await Assert.That(batch.Count).IsLessThanOrEqualTo(10);
  }

  [Test]
  public async Task MaxBatchSizeRespectedAsync() {
    var batchTcs = new TaskCompletionSource<IReadOnlyList<int>>();
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        batchTcs.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 5_000,
        MaxBatchSize = 3,
        ImmediateFlushThreshold = 100  // never via threshold; only via cap
      },
      logger: NullLogger.Instance);

    for (var i = 0; i < 10; i++) {
      await flusher.Writer.WriteAsync(i);
    }

    var batch = await batchTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(batch.Count).IsLessThanOrEqualTo(3);
  }

  [Test]
  public async Task DisposeAsync_CompletesLoopAsync() {
    var flusher = new BatchFlusher<int>(
      flush: (items, _) => Task.CompletedTask,
      options: new BatchFlusherOptions(),
      logger: NullLogger.Instance);

    await flusher.DisposeAsync();
    await Assert.That(flusher.StoppedSignal.IsCompletedSuccessfully).IsTrue();
  }

  /// <summary>
  /// Host teardown disposes the flusher through more than one path (the owning worker's StopAsync
  /// AND container disposal). The second call must be a no-op — cancelling the already-disposed
  /// stop CTS throws ObjectDisposedException and nondeterministically fails whatever test happens
  /// to tear the host down.
  /// </summary>
  [Test]
  public async Task DisposeAsync_CalledTwice_IsIdempotentAsync() {
    var flusher = new BatchFlusher<int>(
      flush: (_, _) => Task.CompletedTask,
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 25,
        MaxBatchSize = 100,
        ImmediateFlushThreshold = 50
      },
      logger: NullLogger.Instance);

    await flusher.DisposeAsync();
    await flusher.DisposeAsync(); // must not throw ObjectDisposedException

    await Assert.That(flusher.StoppedSignal.IsCompletedSuccessfully).IsTrue();
  }
}
