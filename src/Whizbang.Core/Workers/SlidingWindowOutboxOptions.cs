// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="SlidingWindowOutboxBatchStrategy"/>. Defaults match the
/// pump-then-process plan: 50 ms debounce / 1 s hard cap / 100-message batch ceiling per stream.
/// </summary>
/// <docs>extending/internals/outbox-batch-strategy</docs>
public sealed record SlidingWindowOutboxOptions {
  /// <summary>
  /// Debounce window after the last append PER STREAM. Resets on each new arrival for the
  /// same stream. When this elapses without new arrivals, the stream's batch flushes.
  /// Default: 50 ms.
  /// </summary>
  public TimeSpan SlidingWindow { get; set; } = TimeSpan.FromMilliseconds(50);

  /// <summary>
  /// Hard cap on time from the first append in a batch (per stream). A continuously-busy
  /// stream still flushes within this window. Default: 1 s.
  /// </summary>
  public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum messages per stream batch. The stream's batch flushes immediately when this is
  /// reached. Default: 100.
  /// </summary>
  public int MaxSize { get; set; } = 100;

  /// <summary>
  /// A stream's per-stream buffer is evicted when no items have been appended for this duration.
  /// Bounds memory under workloads with many short-lived streams. Default: 30 s.
  /// </summary>
  public TimeSpan IdleEvictionWindow { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How often the idle sweep runs to find and dispose evicted streams. Default: 10 s.
  /// </summary>
  public TimeSpan IdleSweepInterval { get; set; } = TimeSpan.FromSeconds(10);

  /// <summary>
  /// How many times a stream's batch is stored before it is given up on, the first attempt included. Only a
  /// transient database failure (a lost connection, an exhausted connection pool, a command timeout, the server
  /// out of connection slots, a deadlock) is retried; any other failure gives the batch up at once. A batch given up
  /// on is written to the dead-letter table, from where dead-letter recovery stores it again (#1871). Default: 5.
  /// Must be at least 1.
  /// </summary>
  /// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxFlushFailureTests.cs:FlushFailsTransientlyOnEveryAttempt_DeadLettersTheBatchAfterMaxFlushAttemptsAsync</tests>
  public int MaxFlushAttempts { get; set; } = 5;

  /// <summary>
  /// The wait before the first retry of a stream's batch. Each further retry waits twice as long as the one before,
  /// up to <see cref="FlushRetryMaxBackoff"/>. The stream's later batches wait behind it, so its order is kept.
  /// Measured on the strategy's <see cref="TimeProvider"/>. Default: 100 ms.
  /// </summary>
  /// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxFlushFailureTests.cs:FlushFailsTransientlyOnEveryAttempt_DeadLettersTheBatchAfterMaxFlushAttemptsAsync</tests>
  public TimeSpan FlushRetryBackoff { get; set; } = TimeSpan.FromMilliseconds(100);

  /// <summary>
  /// The longest wait between two attempts to store a stream's batch. Default: 5 s.
  /// </summary>
  /// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxFlushFailureTests.cs:FlushRetryBackoff_DoublesUpToTheMaximumAsync</tests>
  public TimeSpan FlushRetryMaxBackoff { get; set; } = TimeSpan.FromSeconds(5);
}
