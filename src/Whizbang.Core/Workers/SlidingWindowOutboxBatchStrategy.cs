// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Default <see cref="IOutboxBatchStrategy"/> — per-stream-keyed sliding-window batcher. Each
/// stream_id has its own bounded channel + drain task. Same-stream messages serialize through
/// one buffer in MessageId order; different streams batch independently in parallel windows.
/// </summary>
/// <remarks>
/// <para>Slice 9 of plans/pump-then-process.md (Half B — emit boundary).</para>
/// <para>
/// Architectural note: this is structurally similar to <see cref="PerStreamSerializer{T}"/> but
/// the processor receives a BATCH per flush window, not one item at a time. Each stream's
/// drain task accumulates items via <see cref="SlidingWindowBatcher{T}"/> (50 ms / 1 s / 100
/// defaults) and invokes the configured <see cref="OutboxBulkFlushCallback"/> with a sorted
/// array. The bulk flush ultimately invokes
/// <see cref="IWorkCoordinator.StoreOutboxMessagesAsync"/> + the SQL
/// <c>_emit_event_store_chain</c> path, which assigns versions per-stream by message_id —
/// closing the inter-emit cursor-inversion gap that the saga fan-out hit in production.
/// </para>
/// <para>
/// Memory bound via idle eviction: streams with no activity for
/// <see cref="SlidingWindowOutboxOptions.IdleEvictionWindow"/> get their buffer disposed. The
/// next message for that stream creates a fresh buffer.
/// </para>
/// <para>
/// A batch is never dropped (#1871). A store that fails with a transient database failure is retried on the same
/// stream's worker, with backoff, up to <see cref="SlidingWindowOutboxOptions.MaxFlushAttempts"/>, so the stream's
/// later batches wait behind it and its order is kept. A batch that still cannot be stored, or that fails with an
/// error a retry cannot fix, or that a stop past its deadline abandons, is handed to the
/// <see cref="OutboxFlushFailureCallback"/> (which writes it to the dead-letter table, from where recovery stores it
/// again), counted, and logged with every message id it held.
/// </para>
/// </remarks>
/// <docs>extending/internals/event-ordering-invariant</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxBatchStrategyTests.cs</tests>
public sealed partial class SlidingWindowOutboxBatchStrategy : IOutboxBatchStrategy {
  /// <summary>
  /// The idle sweeps still running. A sweep evicts a quiet stream and waits for its drain worker, which may still be
  /// flushing the stream's last batch; the stream has left the map by then, so the stop waits for the sweep instead (#1721).
  /// </summary>
  internal DetachedLifecycleWork IdleSweeps { get; } = new();

  private static readonly Guid _defaultStreamKey = Guid.Empty;

  private readonly OutboxBulkFlushCallback _flush;
  private readonly OutboxFlushFailureCallback? _onFlushFailed;
  private readonly WorkCoordinatorMetrics? _metrics;
  private readonly SlidingWindowOutboxOptions _options;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;

  private readonly ConcurrentDictionary<Guid, StreamBuffer> _streams = new();
  private readonly CancellationTokenSource _stopCts = new();
  private readonly ITimer _idleSweepTimer;
  private int _disposed;

  /// <summary>
  /// Creates the strategy with the given flush callback.
  /// </summary>
  /// <param name="flush">Called with each per-stream batch. Typically resolves <see cref="IWorkCoordinator"/> from a DI scope and calls <see cref="IWorkCoordinator.StoreOutboxMessagesAsync"/>.</param>
  /// <param name="logger">Optional logger; flush exceptions get logged at Error.</param>
  /// <param name="options">Tuning knobs; null uses 50 ms / 1 s / 100 defaults.</param>
  /// <param name="timeProvider">Time source. Pass <see cref="TimeProvider.System"/> in production, fake in tests.</param>
  public SlidingWindowOutboxBatchStrategy(
      OutboxBulkFlushCallback flush,
      ILogger<SlidingWindowOutboxBatchStrategy> logger,
      SlidingWindowOutboxOptions? options = null,
      TimeProvider? timeProvider = null)
    : this(flush, logger, options, timeProvider, onFlushFailed: null, metrics: null) {
  }

  /// <summary>
  /// Creates the strategy with the given flush callback and what to do with a batch it cannot store (#1871).
  /// </summary>
  /// <param name="flush">Called with each per-stream batch. Typically resolves <see cref="IWorkCoordinator"/> from a DI scope and calls <see cref="IWorkCoordinator.StoreOutboxMessagesAsync"/>.</param>
  /// <param name="logger">Logger; a retry is logged at Warning and a batch that was not stored at Error, with its message ids.</param>
  /// <param name="options">Tuning knobs; null uses the defaults.</param>
  /// <param name="timeProvider">Time source for the windows and the retry backoff. Pass <see cref="TimeProvider.System"/> in production, fake in tests.</param>
  /// <param name="onFlushFailed">Records a batch that could not be stored; the registered one dead-letters it. Null only logs and counts it.</param>
  /// <param name="metrics">Counts retries and messages that were not stored; null counts nothing.</param>
  /// <docs>internals/outbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxFlushFailureTests.cs</tests>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3427:Method overloads with default parameter values should not overlap", Justification = "The four-parameter constructor is the published one and stays for binary compatibility; this one adds the failure handling and takes every parameter explicitly.")]
  public SlidingWindowOutboxBatchStrategy(
      OutboxBulkFlushCallback flush,
      ILogger<SlidingWindowOutboxBatchStrategy> logger,
      SlidingWindowOutboxOptions? options,
      TimeProvider? timeProvider,
      OutboxFlushFailureCallback? onFlushFailed,
      WorkCoordinatorMetrics? metrics) {
    ArgumentNullException.ThrowIfNull(flush);
    _flush = flush;
    _onFlushFailed = onFlushFailed;
    _metrics = metrics;
    _options = options ?? new SlidingWindowOutboxOptions();
    ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxFlushAttempts, 1);
    _timeProvider = timeProvider ?? TimeProvider.System;
    _logger = logger ?? (ILogger)NullLogger.Instance;

    _idleSweepTimer = _timeProvider.CreateTimer(
      static state => ((SlidingWindowOutboxBatchStrategy)state!)._fireAndForgetIdleSweep(),
      state: this,
      dueTime: _options.IdleSweepInterval,
      period: _options.IdleSweepInterval);
  }

  /// <summary>Active per-stream buffer count — exposed for tests + diagnostics.</summary>
  public int ActiveStreamCount => _streams.Count;

  /// <inheritdoc />
  public async ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    var key = message.StreamId ?? _defaultStreamKey;
    while (true) {
      var buffer = _streams.GetOrAdd(key, k => _createStreamBuffer(k));
      buffer.LastActivity = _timeProvider.GetUtcNow();
      try {
        await buffer.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        return;
      } catch (ChannelClosedException) when (Volatile.Read(ref _disposed) == 0) {
        // The idle sweep evicted this buffer between the lookup and the write; it is out of the map, so the next
        // lookup creates the stream's new buffer. Only a shutdown closes a buffer that is still mapped.
      }
    }
  }

  /// <inheritdoc />
  public async Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }
    await _idleSweepTimer.DisposeAsync().ConfigureAwait(false);
    foreach (var (_, buffer) in _streams) {
      buffer.Writer.TryComplete();
    }
    var workers = _streams.Values.Select(b => b.Worker).ToArray();
    try {
      await Task.WhenAll(workers).WaitAsync(cancellationToken).ConfigureAwait(false);
      // A sweep already under way may hold a stream it evicted, whose worker is still flushing (#1721)
      await IdleSweeps.DrainAsync(cancellationToken).ConfigureAwait(false);
      _stopCts.Dispose();
    } catch (OperationCanceledException) {
      // The caller's deadline passed, so the stop does not wait any longer: a flush that ignores its token would hold
      // the host past its deadline. Each worker gives up its retries and its buffered batches and reports what it
      // abandoned on its own: the count and every message id in the error log, and a dead letter while the provider
      // still serves one (#1871). The source is not disposed: those workers still read it.
      await _stopCts.CancelAsync().ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    await FlushAndStopAsync(CancellationToken.None).ConfigureAwait(false);
  }

  /// <summary>
  /// Test seam: runs one idle-sweep pass synchronously and awaits it. Production relies on the
  /// periodic timer, whose callback is fire-and-forget — so the only way to observe what a sweep
  /// did (or refused to do after shutdown) is to drive one directly. Mirrors
  /// <see cref="PerStreamSerializer{T}.RunIdleSweepNowAsync"/>.
  /// </summary>
  internal Task RunIdleSweepNowForTestAsync() => _runIdleSweepAsync();

  /// <summary>
  /// Test seam: the drain workers of every mapped stream buffer.
  /// </summary>
  /// <remarks>
  /// How a worker's loop ENDED is invisible from outside: a shutdown absorbed by the loop's own
  /// catch and one that escapes it both stop draining, and <see cref="FlushAndStopAsync"/> folds a
  /// faulted worker into the same catch it uses for a caller-canceled shutdown, so it swallows the
  /// difference in production too. The task's final state is the only evidence, and a worker that
  /// faults instead of returning is an unobserved exception nobody ever sees.
  /// </remarks>
  internal Task WhenWorkersStoppedForTests() => Task.WhenAll(_streams.Values.Select(b => b.Worker));

  private StreamBuffer _createStreamBuffer(Guid key) {
    var channel = Channel.CreateBounded<OutboxMessage>(new BoundedChannelOptions(_options.MaxSize * 4) {
      SingleReader = true,
      SingleWriter = false,
      FullMode = BoundedChannelFullMode.Wait,
    });
    var batcherOptions = new SlidingWindowBatcherOptions {
      SlidingWindow = _options.SlidingWindow,
      MaxWait = _options.MaxWait,
      MaxSize = _options.MaxSize,
    };
    var batcher = new SlidingWindowBatcher<OutboxMessage>(channel.Reader, batcherOptions, _timeProvider);
    var buffer = new StreamBuffer(key, channel, _timeProvider.GetUtcNow());
    // No stop token: a worker canceled before it started would leave its stream's messages unreported (#1871).
    buffer.Worker = Task.Run(() => _drainBufferAsync(buffer, batcher), CancellationToken.None);
    return buffer;
  }

  private async Task _drainBufferAsync(StreamBuffer buffer, SlidingWindowBatcher<OutboxMessage> batcher) {
    List<OutboxMessage>? abandoned = null;
    try {
      // The batcher ends when the stream's channel is completed, which every stop and every eviction does first. It
      // takes no stop token, so a stop past its deadline still drains what was buffered, and reports it, rather than
      // discarding a half-filled batch (#1871).
      await foreach (var batch in batcher.ReadBatchesAsync(CancellationToken.None).ConfigureAwait(false)) {
        // Sort by MessageId — locks the producer-side stream-affinity invariant. Concurrent
        // appends to this stream's channel may complete in non-deterministic order; the sort
        // restores message_id (UUIDv7 chronological) order before the SQL chain call.
        var array = batch.ToArray();
        if (array.Length > 1) {
          Array.Sort(array, static (a, b) => a.MessageId.CompareTo(b.MessageId));
        }
        if (_stopCts.IsCancellationRequested || !await _storeOrReportAsync(buffer.Key, array).ConfigureAwait(false)) {
          (abandoned ??= []).AddRange(array);
        }
      }
      if (abandoned is not null) {
        await _reportAsync(new OutboxFlushFailure(buffer.Key, abandoned, OutboxFlushFailureCause.AbandonedAtShutdown, 0, null))
          .ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
      // shutdown
    }
  }

  /// <summary>
  /// Stores one batch, retrying a transient database failure with backoff. A batch that cannot be stored is reported
  /// here. Returns false only when a stop past its deadline abandoned the batch, which the caller reports with the
  /// rest of what the stop abandoned.
  /// </summary>
  private async Task<bool> _storeOrReportAsync(Guid streamKey, OutboxMessage[] batch) {
    // Every attempt returns from its body or waits and tries again, so the loop needs no condition.
    var attempt = 0;
    while (true) {
      attempt++;
      try {
        await _flush(batch, _stopCts.Token).ConfigureAwait(false);
        return true;
      } catch (OperationCanceledException) when (_stopCts.IsCancellationRequested) {
        return false;
      } catch (Exception ex) {
        if (!TransientDatabaseFailure.TryClassify(ex, out var transient)) {
          await _reportAsync(new OutboxFlushFailure(streamKey, batch, OutboxFlushFailureCause.NotRetryable, attempt, ex))
            .ConfigureAwait(false);
          return true;
        }
        if (attempt >= _options.MaxFlushAttempts) {
          await _reportAsync(new OutboxFlushFailure(streamKey, batch, OutboxFlushFailureCause.RetriesExhausted, attempt, ex))
            .ConfigureAwait(false);
          return true;
        }
        var delay = RetryBackoff(attempt);
        _metrics?.OutboxFlushRetries.Add(1, new KeyValuePair<string, object?>(WorkCoordinatorMetrics.REASON_TAG, transient.Reason));
        LogFlushRetrying(_logger, ex, batch.Length, streamKey, transient.Reason, attempt, _options.MaxFlushAttempts, delay);
        try {
          await Task.Delay(delay, _timeProvider, _stopCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          return false;
        }
      }
    }
  }

  /// <summary>
  /// The wait after the given failed attempt: <see cref="SlidingWindowOutboxOptions.FlushRetryBackoff"/>, doubled for
  /// each attempt after the first, never more than <see cref="SlidingWindowOutboxOptions.FlushRetryMaxBackoff"/>.
  /// </summary>
  internal TimeSpan RetryBackoff(int failedAttempt) {
    var doubled = _options.FlushRetryBackoff.TotalMilliseconds * Math.Pow(2, failedAttempt - 1);
    return TimeSpan.FromMilliseconds(Math.Min(doubled, _options.FlushRetryMaxBackoff.TotalMilliseconds));
  }

  /// <summary>
  /// Hands a batch that was not stored to the failure callback (which dead-letters it), counts it, and logs it with
  /// every message id, saying where it went and how to get it back (#1871).
  /// </summary>
  private async Task _reportAsync(OutboxFlushFailure failure) {
    var deadLettered = false;
    if (_onFlushFailed is not null) {
      try {
        // Not the stop token: a stop past its deadline is exactly when the messages need keeping. A provider already
        // disposed fails here and the messages are logged as lost instead.
        deadLettered = await _onFlushFailed(failure, CancellationToken.None).ConfigureAwait(false);
      } catch (Exception ex) {
        LogFailureNotRecorded(_logger, ex, failure.Messages.Count, failure.StreamId);
      }
    }
    _metrics?.OutboxFlushFailedMessages.Add(failure.Messages.Count,
      new KeyValuePair<string, object?>(WorkCoordinatorMetrics.CAUSE_TAG, failure.Cause.ToString()),
      new KeyValuePair<string, object?>(WorkCoordinatorMetrics.OUTCOME_TAG,
        deadLettered ? WorkCoordinatorMetrics.OUTCOME_DEAD_LETTERED : WorkCoordinatorMetrics.OUTCOME_NOT_RECORDED));
    var ids = string.Join(',', failure.Messages.Select(static m => m.MessageId));
    if (deadLettered) {
      LogFlushDeadLettered(_logger, failure.Error, failure.Messages.Count, failure.StreamId, failure.Cause, failure.Attempts, ids);
    } else {
      LogFlushLost(_logger, failure.Error, failure.Messages.Count, failure.StreamId, failure.Cause, failure.Attempts, ids);
    }
  }

  private async Task _runIdleSweepAsync() {
    if (Volatile.Read(ref _disposed) != 0) {
      return;
    }
    var cutoff = _timeProvider.GetUtcNow() - _options.IdleEvictionWindow;
    foreach (var (key, buffer) in _streams) {
      if (buffer.LastActivity > cutoff) {
        continue;
      }
      if (!_streams.TryRemove(KeyValuePair.Create(key, buffer))) {
        continue;
      }
      buffer.Writer.TryComplete();
      try {
        await buffer.Worker.ConfigureAwait(false);
      } catch {
        // Worker errors already logged in _drainBufferAsync; sweep continues.
      }
    }
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Storing {BatchSize} outbox message(s) for stream {StreamKey} failed with a transient {Reason}; attempt {Attempt} of {MaxAttempts}, retrying in {Delay}")]
  private static partial void LogFlushRetrying(ILogger logger, Exception exception, int batchSize, Guid streamKey, string reason, int attempt, int maxAttempts, TimeSpan delay);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "{BatchSize} outbox message(s) for stream {StreamKey} were not stored ({Cause}, {Attempts} attempt(s)) and were written to wh_dead_letters with reason OutboxStoreFailed. Dead-letter recovery stores them again; to do it now, recover the dead letters whose source_id is one of these message ids: {MessageIds}")]
  private static partial void LogFlushDeadLettered(ILogger logger, Exception? exception, int batchSize, Guid streamKey, OutboxFlushFailureCause cause, int attempts, string messageIds);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "{BatchSize} outbox message(s) for stream {StreamKey} were not stored ({Cause}, {Attempts} attempt(s)) and could not be written to wh_dead_letters either, so they exist nowhere else: they are lost unless their source sends them again. Message ids: {MessageIds}")]
  private static partial void LogFlushLost(ILogger logger, Exception? exception, int batchSize, Guid streamKey, OutboxFlushFailureCause cause, int attempts, string messageIds);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "Recording {BatchSize} outbox message(s) for stream {StreamKey} that were not stored failed")]
  private static partial void LogFailureNotRecorded(ILogger logger, Exception exception, int batchSize, Guid streamKey);

  // The timer callback starts the sweep without waiting for it; the sweep is recorded so the stop can (#1721).
  private void _fireAndForgetIdleSweep() {
    IdleSweeps.Track(_runIdleSweepAsync());
  }

  private sealed class StreamBuffer(Guid key, Channel<OutboxMessage> channel, DateTimeOffset createdAt) {
    public Guid Key { get; } = key;
    public ChannelWriter<OutboxMessage> Writer => channel.Writer;
    public DateTimeOffset LastActivity { get; set; } = createdAt;
    public Task Worker { get; set; } = Task.CompletedTask;
  }
}
