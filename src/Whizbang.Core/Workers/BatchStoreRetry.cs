// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>How one batch's store ended (#1871, #1885).</summary>
internal enum BatchStoreOutcome {
  /// <summary>An attempt stored the batch.</summary>
  Stored = 0,

  /// <summary>An attempt failed with an error a retry cannot fix, so the batch was not retried.</summary>
  NotRetryable = 1,

  /// <summary>Every attempt failed with a transient database failure, and the attempts allowed ran out.</summary>
  RetriesExhausted = 2,

  /// <summary>A stop past its deadline canceled the store or the wait before the next attempt.</summary>
  Abandoned = 3,
}

/// <summary>How one batch's store ended, after how many attempts, and with which last failure.</summary>
/// <param name="Outcome">How the store ended.</param>
/// <param name="Attempts">The attempts made.</param>
/// <param name="Error">The last failure; null when the batch was stored or abandoned.</param>
internal readonly record struct BatchStoreResult(BatchStoreOutcome Outcome, int Attempts, Exception? Error);

/// <summary>
/// Stores one batch of a per-stream batcher, retrying a transient database failure with backoff on the calling
/// worker, so the stream's later batches wait behind it and its order is kept. Shared by the outbox (#1871) and inbox
/// (#1885) sliding-window batchers; each decides what a batch that was not stored becomes.
/// </summary>
/// <param name="maxAttempts">How many times a batch is stored before it is given up on, the first attempt included.</param>
/// <param name="backoff">The wait before the first retry; each further retry waits twice as long as the one before.</param>
/// <param name="maxBackoff">The longest wait between two attempts.</param>
/// <param name="timeProvider">The clock the waits are measured on.</param>
/// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxFlushFailureTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowInboxFlushFailureTests.cs</tests>
internal sealed class BatchStoreRetry(int maxAttempts, TimeSpan backoff, TimeSpan maxBackoff, TimeProvider timeProvider) {
  /// <summary>
  /// The wait after the given failed attempt: the backoff doubled for each attempt after the first, never more than
  /// the maximum.
  /// </summary>
  internal TimeSpan Backoff(int failedAttempt) {
    var doubled = backoff.TotalMilliseconds * Math.Pow(2, failedAttempt - 1);
    return TimeSpan.FromMilliseconds(Math.Min(doubled, maxBackoff.TotalMilliseconds));
  }

  /// <summary>
  /// Stores the batch until an attempt succeeds, fails with an error a retry cannot fix, the attempts run out, or
  /// <paramref name="stop"/> cancels the store or the wait. Never throws.
  /// </summary>
  /// <param name="store">One attempt; given <paramref name="stop"/>.</param>
  /// <param name="onRetry">Told about each retry before its wait: the failure, its kind, the attempt that failed and the wait.</param>
  /// <param name="stop">The batcher's stop, canceled by a stop past its deadline.</param>
  internal async Task<BatchStoreResult> StoreAsync(
      Func<CancellationToken, Task> store,
      Action<Exception, string, int, TimeSpan> onRetry,
      CancellationToken stop) {
    // Every attempt returns from its body or waits and tries again, so the loop needs no condition.
    var attempt = 0;
    while (true) {
      attempt++;
      try {
        await store(stop).ConfigureAwait(false);
        return new BatchStoreResult(BatchStoreOutcome.Stored, attempt, null);
      } catch (OperationCanceledException) when (stop.IsCancellationRequested) {
        return new BatchStoreResult(BatchStoreOutcome.Abandoned, attempt, null);
      } catch (Exception ex) {
        if (!TransientDatabaseFailure.TryClassify(ex, out var transient)) {
          return new BatchStoreResult(BatchStoreOutcome.NotRetryable, attempt, ex);
        }
        if (attempt >= maxAttempts) {
          return new BatchStoreResult(BatchStoreOutcome.RetriesExhausted, attempt, ex);
        }
        var delay = Backoff(attempt);
        onRetry(ex, transient.Reason, attempt, delay);
        try {
          await Task.Delay(delay, timeProvider, stop).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          return new BatchStoreResult(BatchStoreOutcome.Abandoned, attempt, null);
        }
      }
    }
  }
}
