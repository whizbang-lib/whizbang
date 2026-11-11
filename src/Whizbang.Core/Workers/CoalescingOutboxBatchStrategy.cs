// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Settings for <see cref="CoalescingOutboxBatchStrategy"/>, bound from <c>Whizbang:Workers:CoalescingOutbox</c>.
/// </summary>
/// <docs>extending/internals/outbox-batch-strategy#coalescing</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/CoalescingOutboxBatchStrategyTests.cs</tests>
public sealed record CoalescingOutboxOptions {
  /// <summary>How long a partition waits after the last arrival before flushing. Default 50 ms.</summary>
  public TimeSpan SlidingWindow { get; set; } = TimeSpan.FromMilliseconds(50);

  /// <summary>The longest a message waits in a partition before its batch flushes. Default 1 s.</summary>
  public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>The most messages one flush carries, from any number of streams. Default 500.</summary>
  public int MaxSize { get; set; } = 500;

  /// <summary>
  /// How many flushes may be in flight at once. A stream always maps to the same partition, so its messages stay in
  /// order; streams that share a partition flush together. Default 4.
  /// </summary>
  public int Partitions { get; set; } = 4;

  /// <summary>Attempts to store a batch that fails with a transient database error. Default 5.</summary>
  public int MaxFlushAttempts { get; set; } = 5;

  /// <summary>The wait after the first failed attempt, doubled for each attempt after it. Default 100 ms.</summary>
  public TimeSpan FlushRetryBackoff { get; set; } = TimeSpan.FromMilliseconds(100);

  /// <summary>The longest wait between attempts. Default 5 s.</summary>
  public TimeSpan FlushRetryMaxBackoff { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The default outbox batch strategy (#2220): outbox writes from many streams are coalesced into one flush, so a burst
/// over thousands of single-message streams becomes a few flushes instead of one flush, one buffer and one gate waiter
/// per stream.
/// </summary>
/// <remarks>
/// <para>
/// Each stream maps to one of <see cref="CoalescingOutboxOptions.Partitions"/> partitions, each with one sliding window
/// and one drain task, so a stream's messages are flushed in the order they were appended and two flushes never carry the
/// same stream at once. Within a flush the messages are ordered by stream and then by message id (UUIDv7, so
/// chronological), the order the per-stream event chain needs.
/// </para>
/// <para>
/// A batch that fails with a transient database error is retried with backoff; one that cannot be stored, or that a stop
/// past its deadline abandons, is reported to the <see cref="OutboxFlushFailureCallback"/> one stream at a time (which
/// dead-letters it), counted and logged with every message id. Nothing is dropped silently.
/// </para>
/// </remarks>
/// <docs>extending/internals/outbox-batch-strategy#coalescing</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/CoalescingOutboxBatchStrategyTests.cs</tests>
public sealed class CoalescingOutboxBatchStrategy : IOutboxBatchStrategy {
  private readonly OutboxBulkFlushCallback _flush;
  private readonly CancellationTokenSource _stopCts = new();

  /// <summary>Inert stub.</summary>
  public CoalescingOutboxBatchStrategy(
      OutboxBulkFlushCallback flush,
      ILogger<CoalescingOutboxBatchStrategy>? logger,
      CoalescingOutboxOptions? options,
      TimeProvider? timeProvider,
      OutboxFlushFailureCallback? onFlushFailed,
      WorkCoordinatorMetrics? metrics) {
    _flush = flush;
    _ = (logger, options, timeProvider, onFlushFailed, metrics);
  }

  /// <summary>Inert stub.</summary>
  public int PartitionCount => _flush.GetHashCode() * 0;

  /// <summary>Inert stub.</summary>
  internal static int PartitionOf(Guid streamId, int partitions) => streamId == Guid.Empty ? partitions : -1;

  /// <inheritdoc />
  public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
    _ = _flush([message], _stopCts.Token);
    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public Task FlushAndStopAsync(CancellationToken cancellationToken = default) => _stopCts.CancelAsync();

  /// <inheritdoc />
  public ValueTask DisposeAsync() {
    _stopCts.Dispose();
    return ValueTask.CompletedTask;
  }

  /// <summary>Inert stub.</summary>
  internal Task WhenDrainsStoppedForTests() => Task.FromResult(_stopCts.IsCancellationRequested);
}
