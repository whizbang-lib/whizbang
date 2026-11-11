// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
public sealed partial class CoalescingOutboxBatchStrategy : IOutboxBatchStrategy {
  private readonly OutboxBulkFlushCallback _flush;
  private readonly OutboxFlushFailureCallback? _onFlushFailed;
  private readonly WorkCoordinatorMetrics? _metrics;
  private readonly BatchStoreRetry _retry;
  private readonly ILogger _logger;
  private readonly int _maxFlushAttempts;
  private readonly Channel<OutboxMessage>[] _channels;
  private readonly Task[] _workers;
  private readonly CancellationTokenSource _stopCts = new();
  private int _disposed;

  /// <summary>Creates the strategy and starts one drain per partition.</summary>
  /// <param name="flush">Stores one batch.</param>
  /// <param name="logger">Retries are logged at Warning, a batch that was not stored at Information or Error.</param>
  /// <param name="options">Tuning; null uses the defaults.</param>
  /// <param name="timeProvider">Time source for the windows and the retry backoff; null uses the system clock.</param>
  /// <param name="onFlushFailed">Records a batch that could not be stored; null only logs and counts it.</param>
  /// <param name="metrics">Counts retries and messages that were not stored; null counts nothing.</param>
  public CoalescingOutboxBatchStrategy(
      OutboxBulkFlushCallback flush,
      ILogger<CoalescingOutboxBatchStrategy>? logger,
      CoalescingOutboxOptions? options,
      TimeProvider? timeProvider,
      OutboxFlushFailureCallback? onFlushFailed,
      WorkCoordinatorMetrics? metrics) {
    ArgumentNullException.ThrowIfNull(flush);
    var settings = options ?? new CoalescingOutboxOptions();
    ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxFlushAttempts, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(settings.Partitions, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxSize, 1);
    var time = timeProvider ?? TimeProvider.System;
    _flush = flush;
    _onFlushFailed = onFlushFailed;
    _metrics = metrics;
    _logger = logger ?? (ILogger)NullLogger.Instance;
    _maxFlushAttempts = settings.MaxFlushAttempts;
    _retry = new BatchStoreRetry(settings.MaxFlushAttempts, settings.FlushRetryBackoff, settings.FlushRetryMaxBackoff, time);
    var batcherOptions = new SlidingWindowBatcherOptions {
      SlidingWindow = settings.SlidingWindow,
      MaxWait = settings.MaxWait,
      MaxSize = settings.MaxSize,
    };
    _channels = new Channel<OutboxMessage>[settings.Partitions];
    _workers = new Task[settings.Partitions];
    for (var i = 0; i < settings.Partitions; i++) {
      var channel = Channel.CreateUnbounded<OutboxMessage>(new UnboundedChannelOptions { SingleReader = true });
      var batcher = new SlidingWindowBatcher<OutboxMessage>(channel.Reader, batcherOptions, time);
      _channels[i] = channel;
      // No ambient context: a drain serves every caller of its partition, not the one that happened to start it (#1883).
      _workers[i] = AmbientFreeTask.Run(() => _drainAsync(batcher), CancellationToken.None);
    }
  }

  /// <summary>How many partitions flush in parallel.</summary>
  public int PartitionCount => _channels.Length;

  /// <summary>The partition a stream's messages go to; the same stream always maps to the same one.</summary>
  internal static int PartitionOf(Guid streamId, int partitions) => (int)((uint)streamId.GetHashCode() % (uint)partitions);

  /// <inheritdoc />
  /// <remarks>
  /// The partition's buffer is unbounded: what it may hold is bounded by the process-wide batch buffer every registered
  /// strategy is wrapped in.
  /// </remarks>
  public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    ArgumentNullException.ThrowIfNull(message);
    var channel = _channels[PartitionOf(message.StreamId ?? Guid.Empty, _channels.Length)];
    return channel.Writer.WriteAsync(message, cancellationToken);
  }

  /// <inheritdoc />
  public async Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }
    foreach (var channel in _channels) {
      channel.Writer.TryComplete();
    }
    try {
      await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false);
      _stopCts.Dispose();
    } catch (OperationCanceledException) {
      // Past the caller's deadline: each drain gives up its retries and reports what it abandoned on its own.
      await _stopCts.CancelAsync().ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    await FlushAndStopAsync(CancellationToken.None).ConfigureAwait(false);
  }

  /// <summary>Test seam: every partition's drain, to see that each has finished.</summary>
  internal Task WhenDrainsStoppedForTests() => Task.WhenAll(_workers);

  private async Task _drainAsync(SlidingWindowBatcher<OutboxMessage> batcher) {
    List<OutboxMessage>? abandoned = null;
    // The batcher ends when the partition's channel is completed, which every stop does first; with no stop token of
    // its own it drains what was buffered even past the stop's deadline, and that is reported, never discarded.
    await foreach (var batch in batcher.ReadBatchesAsync(CancellationToken.None).ConfigureAwait(false)) {
      var ordered = batch.OrderBy(static m => m.StreamId ?? Guid.Empty).ThenBy(static m => m.MessageId).ToArray();
      if (_stopCts.IsCancellationRequested || !await _storeOrReportAsync(ordered).ConfigureAwait(false)) {
        (abandoned ??= []).AddRange(ordered);
      }
    }
    if (abandoned is not null) {
      await _reportAsync(abandoned, OutboxFlushFailureCause.AbandonedAtShutdown, 0, null).ConfigureAwait(false);
    }
  }

  /// <summary>Stores one batch with retries; returns false only when a stop past its deadline abandoned it.</summary>
  private async Task<bool> _storeOrReportAsync(OutboxMessage[] batch) {
    var result = await _retry.StoreAsync(
      ct => _flush(batch, ct),
      (ex, reason, attempt, delay) => {
        _metrics?.OutboxFlushRetries.Add(1, new KeyValuePair<string, object?>(WorkCoordinatorMetrics.REASON_TAG, reason));
        LogFlushRetrying(_logger, ex, batch.Length, reason, attempt, _maxFlushAttempts, delay);
      },
      _stopCts.Token).ConfigureAwait(false);
    var cause = result.Outcome switch {
      BatchStoreOutcome.NotRetryable => OutboxFlushFailureCause.NotRetryable,
      BatchStoreOutcome.RetriesExhausted => OutboxFlushFailureCause.RetriesExhausted,
      _ => (OutboxFlushFailureCause?)null,
    };
    if (cause is { } failed) {
      await _reportAsync(batch, failed, result.Attempts, result.Error).ConfigureAwait(false);
    }
    return result.Outcome != BatchStoreOutcome.Abandoned;
  }

  /// <summary>Reports messages that were not stored, one stream at a time, as the failure record names one stream.</summary>
  private async Task _reportAsync(IEnumerable<OutboxMessage> messages, OutboxFlushFailureCause cause, int attempts, Exception? error) {
    foreach (var stream in messages.GroupBy(static m => m.StreamId ?? Guid.Empty)) {
      var failure = new OutboxFlushFailure(stream.Key, [.. stream], cause, attempts, error);
      var deadLettered = await _recordAsync(failure).ConfigureAwait(false);
      _metrics?.OutboxFlushFailedMessages.Add(failure.Messages.Count,
        new KeyValuePair<string, object?>(WorkCoordinatorMetrics.CAUSE_TAG, cause.ToString()),
        new KeyValuePair<string, object?>(WorkCoordinatorMetrics.OUTCOME_TAG,
          deadLettered ? WorkCoordinatorMetrics.OUTCOME_DEAD_LETTERED : WorkCoordinatorMetrics.OUTCOME_NOT_RECORDED));
      var ids = string.Join(',', failure.Messages.Select(static m => m.MessageId));
      if (deadLettered) {
        LogFlushDeadLettered(_logger, error, failure.Messages.Count, stream.Key, cause, attempts, ids);
      } else {
        LogFlushLost(_logger, error, failure.Messages.Count, stream.Key, cause, attempts, ids);
      }
    }
  }

  /// <summary>Hands a failure to the callback; one that throws is logged and the batch reported as lost.</summary>
  private async Task<bool> _recordAsync(OutboxFlushFailure failure) {
    if (_onFlushFailed is null) {
      return false;
    }
    try {
      return await _onFlushFailed(failure, CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) {
      LogFailureNotRecorded(_logger, ex, failure.Messages.Count, failure.StreamId);
      return false;
    }
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Storing a coalesced batch of {BatchSize} outbox message(s) failed with a transient {Reason}; attempt {Attempt} of {MaxAttempts}, retrying in {Delay}")]
  private static partial void LogFlushRetrying(ILogger logger, Exception exception, int batchSize, string reason, int attempt, int maxAttempts, TimeSpan delay);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "{BatchSize} outbox message(s) for stream {StreamKey} were not stored ({Cause}, {Attempts} attempt(s)) and were written to wh_dead_letters with reason OutboxStoreFailed. Dead-letter recovery stores them again; to do it now, recover the dead letters whose source_id is one of these message ids: {MessageIds}")]
  private static partial void LogFlushDeadLettered(ILogger logger, Exception? exception, int batchSize, Guid streamKey, OutboxFlushFailureCause cause, int attempts, string messageIds);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "{BatchSize} outbox message(s) for stream {StreamKey} were not stored ({Cause}, {Attempts} attempt(s)) and could not be written to wh_dead_letters either, so they exist nowhere else: they are lost unless their source sends them again. Message ids: {MessageIds}")]
  private static partial void LogFlushLost(ILogger logger, Exception? exception, int batchSize, Guid streamKey, OutboxFlushFailureCause cause, int attempts, string messageIds);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "Recording {BatchSize} outbox message(s) for stream {StreamKey} that were not stored failed")]
  private static partial void LogFailureNotRecorded(ILogger logger, Exception exception, int batchSize, Guid streamKey);
}
