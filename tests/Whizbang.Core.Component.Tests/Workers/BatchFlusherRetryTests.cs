// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <remarks>
/// The flusher runs on a fake clock (#1868): each retry backoff is a timer on that clock, so a test reads the backoff
/// the flusher chose from the timer it armed, and only then moves the clock past it. Nothing waits on real time.
/// </remarks>
public class BatchFlusherRetryTests {
  private const int FIRST_BACKOFF_MS = 100;
  private const int MAX_BACKOFF_MS = 150;

  /// <summary>
  /// Flushes on item COUNT, not on time: the coalesce window never closes on a clock the test does not move for it,
  /// and the immediate-flush threshold is exactly the number of items the test writes, so the batch composition does
  /// not depend on scheduling.
  /// </summary>
  private static BatchFlusherOptions _retryOptions(int maxAttempts, int flushAt) => new() {
    CoalesceWindowMs = 10_000,
    MaxBatchSize = 100,
    ImmediateFlushThreshold = flushAt,
    MaxFlushAttempts = maxAttempts,
    FlushRetryBackoffMs = FIRST_BACKOFF_MS,
    FlushRetryMaxBackoffMs = MAX_BACKOFF_MS,
  };

  [Test]
  [Timeout(30000)]
  public async Task FlushFailsOnce_RetriesTheSameBatchAndDeliversItAsync(CancellationToken cancellationToken) {
    var calls = new List<IReadOnlyList<int>>();
    var delivered = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new CapturingLogger<BatchFlusher<int>>();
    var clock = new ArmingClock();
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        lock (calls) { calls.Add([.. items]); }
        if (calls.Count == 1) {
          throw new InvalidOperationException("transient");
        }
        delivered.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: _retryOptions(maxAttempts: 5, flushAt: 3),
      logger: logger,
      timeProvider: clock);

    await flusher.Writer.WriteAsync(1, cancellationToken);
    await flusher.Writer.WriteAsync(2, cancellationToken);
    await flusher.Writer.WriteAsync(3, cancellationToken);
    // The retry waits out its backoff on the flusher's clock
    await clock.WaitForArmedAsync(TimeSpan.FromMilliseconds(FIRST_BACKOFF_MS), cancellationToken);
    await Assert.That(delivered.Task.IsCompleted).IsFalse()
      .Because("the retry has not run while its backoff is still pending");
    clock.Advance(TimeSpan.FromMilliseconds(FIRST_BACKOFF_MS));
    var items = await delivered.Task.WaitAsync(cancellationToken);
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
  [Timeout(30000)]
  public async Task FlushAlwaysFails_DropsAfterMaxAttemptsAndNamesTheConsequenceAsync(CancellationToken cancellationToken) {
    var attempts = 0;
    var logger = new CapturingLogger<BatchFlusher<int>>();
    var clock = new ArmingClock();
    await using var flusher = new BatchFlusher<int>(
      flush: (_, _) => {
        Interlocked.Increment(ref attempts);
        throw new InvalidOperationException("still broken");
      },
      options: _retryOptions(maxAttempts: 3, flushAt: 2),
      logger: logger,
      timeProvider: clock);

    await flusher.Writer.WriteAsync(10, cancellationToken);
    await flusher.Writer.WriteAsync(20, cancellationToken);
    // Two backoffs separate three attempts: the first, then double it, capped at the maximum
    await clock.WaitForArmedAsync(TimeSpan.FromMilliseconds(FIRST_BACKOFF_MS), cancellationToken);
    clock.Advance(TimeSpan.FromMilliseconds(FIRST_BACKOFF_MS));
    await clock.WaitForArmedAsync(TimeSpan.FromMilliseconds(MAX_BACKOFF_MS), cancellationToken);
    clock.Advance(TimeSpan.FromMilliseconds(MAX_BACKOFF_MS));
    var error = await logger.WaitForAsync(e => e.Level == LogLevel.Error).WaitAsync(cancellationToken);
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
