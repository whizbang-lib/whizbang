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
/// Default <see cref="IInboxBatchStrategy"/> — per-stream-keyed sliding-window batcher. Each
/// stream_id has its own bounded channel + drain task. Same-stream inbox messages serialize
/// through one buffer; different streams batch independently in parallel windows. Mirrors
/// <see cref="SlidingWindowOutboxBatchStrategy"/> (slice 9, Half B) but applied at the
/// receive boundary.
/// </summary>
/// <remarks>
/// <para>Slice 23. Replaces the pre-slice-23 single global channel architecture.</para>
/// <para>
/// Motivation: under cross-service fan-in (saga aggregates receiving events from N
/// producers concurrently), the prior global-channel inbox flushed everything in one
/// 50ms window. Two transport messages for the same stream arriving more than 50 ms apart
/// landed in DIFFERENT inbox flushes. Each batch was internally sorted by MessageId
/// (slice 18b) but cross-batch ordering was arrival order — guaranteed to deviate from
/// MessageId order on fan-in streams. The result was thousands of cursor inversions per
/// production bulk-import run, mostly on BulkImportOrchestration / Order / saga streams.
/// </para>
/// <para>
/// Per-stream-keyed buffer + 300 ms / 3 s sliding window: messages for the same stream
/// coalesce across transport arrivals within the window, get sorted by MessageId at flush
/// (the slice-18b sort survives unchanged), and apply in correct order downstream — no
/// inversion. Different streams remain fully parallel.
/// </para>
/// <para>
/// Memory bound via idle eviction: streams with no messages for
/// <see cref="SlidingWindowInboxOptions.IdleEvictionWindow"/> get their buffer disposed.
/// The next message for that stream creates a fresh buffer. Mirrors the outbox eviction
/// policy.
/// </para>
/// <para>
/// Messages with <c>StreamId == null</c> (broadcast-style, no aggregate identity) route to
/// a single default buffer keyed by <see cref="Guid.Empty"/> — they don't need stream
/// affinity, and grouping them avoids creating a buffer per null-stream message.
/// </para>
/// <para>
/// A batch is never dropped (#1885). <see cref="AppendAsync"/> returns once the message is buffered, before it is
/// stored, so whoever appended it has already been told it was accepted and may already have settled it with its
/// broker: the batcher holds no acknowledgement it could withhold to make the broker deliver it again. A transport's
/// own receive path never uses this batcher; it stores each delivery before it settles it, so a failed store there
/// leaves the delivery unsettled and the broker re-delivers it. Here, a store that fails with a transient database
/// failure is retried on the stream's own worker, with backoff, up to
/// <see cref="SlidingWindowInboxOptions.MaxFlushAttempts"/>, so the stream's later batches wait behind it and its order
/// is kept. A batch that still cannot be stored, fails with an error a retry cannot fix, or is abandoned by a stop past
/// its deadline is handed to the <see cref="InboxFlushFailureCallback"/> (which writes it to the dead-letter table, from
/// where recovery stores it through the inbox store), counted, and logged with every message id it held.
/// </para>
/// </remarks>
/// <docs>extending/internals/event-ordering-invariant</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowInboxBatchStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowInboxFlushFailureTests.cs</tests>
public sealed partial class SlidingWindowInboxBatchStrategy : IInboxBatchStrategy {
  /// <summary>
  /// The idle sweeps still running. A sweep evicts a quiet stream and waits for its drain worker, which may still be
  /// flushing the stream's last batch; the stream has left the map by then, so the stop waits for the sweep instead (#1721).
  /// </summary>
  internal Messaging.DetachedLifecycleWork IdleSweeps { get; } = new();
  private static readonly Guid _defaultStreamKey = Guid.Empty;

  private readonly InboxBulkFlushCallback _flush;
  private readonly InboxFlushFailureCallback? _onFlushFailed;
  private readonly WorkCoordinatorMetrics? _metrics;
  private readonly SlidingWindowInboxOptions _options;
  private readonly TimeProvider _timeProvider;
  private readonly BatchStoreRetry _retry;
  private readonly ILogger _logger;

  private readonly ConcurrentDictionary<Guid, StreamBuffer> _streams = new();
  private readonly CancellationTokenSource _stopCts = new();
  private readonly ITimer _idleSweepTimer;
  private int _disposed;

  /// <summary>
  /// Creates the strategy with the given flush callback and (optionally) tuned options + clock.
  /// </summary>
  /// <param name="flush">Called with each per-stream batch. Typically resolves <see cref="IWorkCoordinator"/> from a DI scope and calls <see cref="IWorkCoordinator.StoreInboxMessagesAsync"/>.</param>
  /// <param name="logger">Optional logger; flush exceptions get logged at Error.</param>
  /// <param name="options">Tuning knobs; null uses the 300 ms / 3 s / 1000 defaults.</param>
  /// <param name="timeProvider">Time source. Pass <see cref="TimeProvider.System"/> in production, fake in tests.</param>
  public SlidingWindowInboxBatchStrategy(
      InboxBulkFlushCallback flush,
      ILogger<SlidingWindowInboxBatchStrategy> logger,
      SlidingWindowInboxOptions? options = null,
      TimeProvider? timeProvider = null)
    : this(flush, logger, options, timeProvider, onFlushFailed: null, metrics: null) {
  }

  /// <summary>
  /// Creates the strategy with the given flush callback and what to do with a batch it cannot store (#1885).
  /// </summary>
  /// <param name="flush">Called with each per-stream batch. Typically resolves <see cref="IWorkCoordinator"/> from a DI scope and calls <see cref="IWorkCoordinator.StoreInboxMessagesAsync"/>.</param>
  /// <param name="logger">Logger; a retry is logged at Warning and a batch that was not stored at Error, with its message ids.</param>
  /// <param name="options">Tuning knobs; null uses the defaults.</param>
  /// <param name="timeProvider">Time source for the windows and the retry backoff. Pass <see cref="TimeProvider.System"/> in production, fake in tests.</param>
  /// <param name="onFlushFailed">Records a batch that could not be stored; the registered one dead-letters it. Null only logs and counts it.</param>
  /// <param name="metrics">Counts retries and messages that were not stored; null counts nothing.</param>
  /// <docs>extending/internals/inbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowInboxFlushFailureTests.cs</tests>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3427:Method overloads with default parameter values should not overlap", Justification = "The four-parameter constructor is the published one and stays for binary compatibility; this one adds the failure handling and takes every parameter explicitly.")]
  public SlidingWindowInboxBatchStrategy(
      InboxBulkFlushCallback flush,
      ILogger<SlidingWindowInboxBatchStrategy> logger,
      SlidingWindowInboxOptions? options,
      TimeProvider? timeProvider,
      InboxFlushFailureCallback? onFlushFailed,
      WorkCoordinatorMetrics? metrics) {
    ArgumentNullException.ThrowIfNull(flush);
    _flush = flush;
    _onFlushFailed = onFlushFailed;
    _metrics = metrics;
    _options = options ?? new SlidingWindowInboxOptions();
    ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxFlushAttempts, 1);
    _timeProvider = timeProvider ?? TimeProvider.System;
    _retry = new BatchStoreRetry(_options.MaxFlushAttempts, _options.FlushRetryBackoff, _options.FlushRetryMaxBackoff, _timeProvider);
    _logger = logger ?? (ILogger)NullLogger.Instance;

    _idleSweepTimer = _timeProvider.CreateTimer(
      static state => ((SlidingWindowInboxBatchStrategy)state!)._fireAndForgetIdleSweep(),
      state: this,
      dueTime: _options.IdleSweepInterval,
      period: _options.IdleSweepInterval);
  }

  /// <summary>Active per-stream buffer count — exposed for tests + diagnostics.</summary>
  public int ActiveStreamCount => _streams.Count;

  /// <inheritdoc />
  public async ValueTask AppendAsync(InboxMessage message, CancellationToken cancellationToken = default) {
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
      // The caller's deadline passed, so the stop does not wait any longer. Each worker gives up its retries and its
      // buffered batches and reports what it abandoned on its own (#1885). The source is not disposed: those workers
      // still read it.
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
  /// <see cref="SlidingWindowOutboxBatchStrategy.RunIdleSweepNowForTestAsync"/>.
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

  /// <summary>
  /// The wait after the given failed attempt: <see cref="SlidingWindowInboxOptions.FlushRetryBackoff"/>, doubled for
  /// each attempt after the first, never more than <see cref="SlidingWindowInboxOptions.FlushRetryMaxBackoff"/>.
  /// </summary>
  internal TimeSpan RetryBackoff(int failedAttempt) => _retry.Backoff(failedAttempt);

  private StreamBuffer _createStreamBuffer(Guid key) {
    var channel = Channel.CreateBounded<InboxMessage>(new BoundedChannelOptions(_options.MaxSize * 4) {
      SingleReader = true,
      SingleWriter = false,
      FullMode = BoundedChannelFullMode.Wait,
    });
    var batcherOptions = new SlidingWindowBatcherOptions {
      SlidingWindow = _options.SlidingWindow,
      MaxWait = _options.MaxWait,
      MaxSize = _options.MaxSize,
    };
    var batcher = new SlidingWindowBatcher<InboxMessage>(channel.Reader, batcherOptions, _timeProvider);
    var buffer = new StreamBuffer(key, channel, _timeProvider.GetUtcNow());
    // No stop token: a worker canceled before it started would leave its stream's messages unreported (#1885). No
    // ambient context either: the worker serves every caller of the stream, not the one whose append started it (#1883).
    buffer.Worker = AmbientFreeTask.Run(() => _drainBufferAsync(buffer, batcher), CancellationToken.None);
    return buffer;
  }

  private async Task _drainBufferAsync(StreamBuffer buffer, SlidingWindowBatcher<InboxMessage> batcher) {
    List<InboxMessage>? abandoned = null;
    try {
      // The batcher ends when the stream's channel is completed, which every stop and every eviction does first. It
      // takes no stop token, so a stop past its deadline still drains what was buffered, and reports it, rather than
      // discarding a half-filled batch (#1885).
      await foreach (var batch in batcher.ReadBatchesAsync(CancellationToken.None).ConfigureAwait(false)) {
        // Slice 18b: sort by MessageId (UUIDv7 chronological) before flushing. With the
        // slice-23 per-stream buffer this sort now covers cross-transport-message events
        // for the same stream — fan-in saga aggregates no longer see late events because
        // the window holds them until quiet, then flushes them all in MessageId order.
        InboxMessage[] array = [.. batch];
        if (array.Length > 1) {
          Array.Sort(array, static (a, b) => a.MessageId.CompareTo(b.MessageId));
        }
        if (_stopCts.IsCancellationRequested || !await _storeOrReportAsync(buffer.Key, array).ConfigureAwait(false)) {
          (abandoned ??= []).AddRange(array);
        }
      }
      if (abandoned is not null) {
        await _reportAsync(new InboxFlushFailure(buffer.Key, abandoned, InboxFlushFailureCause.AbandonedAtShutdown, 0, null))
          .ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
      // A log sink bound to the host's shutdown token throws this while the host stops; the worker ends quietly
      // rather than faulting into an exception nobody observes.
    }
  }

  /// <summary>
  /// Stores one batch, retrying a transient database failure with backoff. A batch that cannot be stored is reported
  /// here. Returns false only when a stop past its deadline abandoned the batch, which the caller reports with the
  /// rest of what the stop abandoned.
  /// </summary>
  private async Task<bool> _storeOrReportAsync(Guid streamKey, InboxMessage[] batch) {
    var result = await _retry.StoreAsync(
      ct => _flush(batch, ct),
      (ex, reason, attempt, delay) => {
        _metrics?.InboxFlushRetries.Add(1, new KeyValuePair<string, object?>(WorkCoordinatorMetrics.REASON_TAG, reason));
        LogFlushRetrying(ex, batch.Length, streamKey, reason, attempt, _options.MaxFlushAttempts, delay);
      },
      _stopCts.Token).ConfigureAwait(false);
    var cause = result.Outcome switch {
      BatchStoreOutcome.NotRetryable => InboxFlushFailureCause.NotRetryable,
      BatchStoreOutcome.RetriesExhausted => InboxFlushFailureCause.RetriesExhausted,
      _ => (InboxFlushFailureCause?)null,
    };
    if (cause is { } failed) {
      await _reportAsync(new InboxFlushFailure(streamKey, batch, failed, result.Attempts, result.Error)).ConfigureAwait(false);
    }
    return result.Outcome != BatchStoreOutcome.Abandoned;
  }

  /// <summary>
  /// Hands a batch that was not stored to the failure callback (which dead-letters it), counts it, and logs it with
  /// every message id, saying where it went and how to get it back (#1885).
  /// </summary>
  private async Task _reportAsync(InboxFlushFailure failure) {
    var deadLettered = await _recordAsync(failure).ConfigureAwait(false);
    _metrics?.InboxFlushFailedMessages.Add(failure.Messages.Count,
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

  /// <summary>
  /// Hands the batch to the failure callback. Not with the stop token: a stop past its deadline is exactly when the
  /// messages need keeping. A callback that throws (a provider already disposed) is logged, and the batch is then
  /// reported as lost.
  /// </summary>
  private async Task<bool> _recordAsync(InboxFlushFailure failure) {
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
    Message = "Storing {BatchSize} inbox message(s) for stream {StreamKey} failed with a transient {Reason}; attempt {Attempt} of {MaxAttempts}, retrying in {Delay}")]
  private partial void LogFlushRetrying(Exception exception, int batchSize, Guid streamKey, string reason, int attempt, int maxAttempts, TimeSpan delay);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "{BatchSize} inbox message(s) for stream {StreamKey} were not stored ({Cause}, {Attempts} attempt(s)) and were written to wh_dead_letters with reason InboxStoreFailed. Dead-letter recovery stores them through the inbox store; to do it now, recover the dead letters whose source_id is one of these message ids: {MessageIds}")]
  private static partial void LogFlushDeadLettered(ILogger logger, Exception? exception, int batchSize, Guid streamKey, InboxFlushFailureCause cause, int attempts, string messageIds);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "{BatchSize} inbox message(s) for stream {StreamKey} were not stored ({Cause}, {Attempts} attempt(s)) and could not be written to wh_dead_letters either: they are lost unless their broker delivers them again. Message ids: {MessageIds}")]
  private static partial void LogFlushLost(ILogger logger, Exception? exception, int batchSize, Guid streamKey, InboxFlushFailureCause cause, int attempts, string messageIds);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "Recording {BatchSize} inbox message(s) for stream {StreamKey} that were not stored failed")]
  private static partial void LogFailureNotRecorded(ILogger logger, Exception exception, int batchSize, Guid streamKey);

  // The timer callback starts the sweep without waiting for it; the sweep is recorded so the stop can (#1721).
  private void _fireAndForgetIdleSweep() {
    IdleSweeps.Track(_runIdleSweepAsync());
  }

  private sealed class StreamBuffer(Guid key, Channel<InboxMessage> channel, DateTimeOffset createdAt) {
    public Guid Key { get; } = key;
    public ChannelWriter<InboxMessage> Writer => channel.Writer;
    public DateTimeOffset LastActivity { get; set; } = createdAt;
    public Task Worker { get; set; } = Task.CompletedTask;
  }
}
