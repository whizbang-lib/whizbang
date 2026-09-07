using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A failed flush used to discard its batch ("items lost"). The items are completions, lease renewals
/// and failures, whose loss leaves rows leased until expiry and re-claimed afterwards, which is how a
/// transient timeout turned into lease churn under a bulk import. The flusher now retries the same
/// batch in place (the flush is idempotent by contract) and only drops it after
/// <see cref="BatchFlusherOptions.MaxFlushAttempts"/> consecutive failures, at Error, naming the
/// consequence.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/BatchFlusher.cs</code-under-test>
public class BatchFlusherRetryTests {
  /// <summary>
  /// Flushes on item COUNT, not on time: the coalesce window is long enough never to elapse in a test,
  /// and the immediate-flush threshold is exactly the number of items the test writes, so the batch
  /// composition does not depend on scheduling.
  /// </summary>
  private static BatchFlusherOptions _fastRetryOptions(int maxAttempts, int flushAt) => new() {
    CoalesceWindowMs = 10_000,
    MaxBatchSize = 100,
    ImmediateFlushThreshold = flushAt,
    MaxFlushAttempts = maxAttempts,
    FlushRetryBackoffMs = 1,
    FlushRetryMaxBackoffMs = 2,
  };

  [Test]
  public async Task FlushFailsOnce_RetriesTheSameBatchAndDeliversItAsync() {
    var calls = new List<IReadOnlyList<int>>();
    var delivered = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new CapturingLogger<BatchFlusher<int>>();
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        lock (calls) { calls.Add([.. items]); }
        if (calls.Count == 1) {
          throw new InvalidOperationException("transient");
        }
        delivered.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: _fastRetryOptions(maxAttempts: 5, flushAt: 3),
      logger: logger);

    await flusher.Writer.WriteAsync(1);
    await flusher.Writer.WriteAsync(2);
    await flusher.Writer.WriteAsync(3);
    var items = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    // The counters are updated after the flush callback returns; draining the flusher (idempotent
    // dispose) is the completion signal that makes reading them deterministic.
    await flusher.DisposeAsync();

    await Assert.That(items).IsEquivalentTo([1, 2, 3])
      .Because("the retry carries the SAME batch; nothing is lost and nothing is reordered");
    await Assert.That(calls.Count).IsEqualTo(2);
    await Assert.That(flusher.ItemsFlushed).IsEqualTo(3L);
    await Assert.That(flusher.ItemsDropped).IsEqualTo(0L);
    await Assert.That(logger.Snapshot().Any(e => e.Level == LogLevel.Warning && e.Message.Contains("retrying", StringComparison.Ordinal))).IsTrue()
      .Because("a retried flush is visible, at Warning, not silent");
  }

  [Test]
  public async Task FlushAlwaysFails_DropsAfterMaxAttemptsAndNamesTheConsequenceAsync() {
    var attempts = 0;
    var logger = new CapturingLogger<BatchFlusher<int>>();
    await using var flusher = new BatchFlusher<int>(
      flush: (_, _) => {
        Interlocked.Increment(ref attempts);
        throw new InvalidOperationException("still broken");
      },
      options: _fastRetryOptions(maxAttempts: 3, flushAt: 2),
      logger: logger);

    await flusher.Writer.WriteAsync(10);
    await flusher.Writer.WriteAsync(20);
    var error = await logger.WaitForAsync(e => e.Level == LogLevel.Error).WaitAsync(TimeSpan.FromSeconds(10));
    await flusher.DisposeAsync();

    await Assert.That(attempts).IsEqualTo(3)
      .Because("MaxFlushAttempts bounds the retries; a permanently broken flush must not spin forever");
    await Assert.That(flusher.ItemsDropped).IsEqualTo(2L);
    await Assert.That(flusher.ItemsFlushed).IsEqualTo(0L);
    await Assert.That(error.Exception).IsNotNull();
    await Assert.That(error.Message).Contains("dropped")
      .Because("the drop is an Error with the cause attached");
    await Assert.That(error.Message).Contains("lease")
      .Because("the log states what happens to the rows behind the dropped items");
  }
}
