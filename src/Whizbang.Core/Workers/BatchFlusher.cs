// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Generic Nagle-style batch flusher. Reads items from a bounded channel; on first
/// arrival, coalesces additional items for up to <c>CoalesceWindowMs</c> or until
/// <c>MaxBatchSize</c> is reached, then invokes <c>FlushAsync</c>.
/// Used by Phase C flush workers (outbox completions, perspective completions,
/// failures, lease renewals) so each shares the same coalescing semantics.
/// Phase C of work-pump decomposition.
/// </summary>
/// <typeparam name="T">Item type the channel carries.</typeparam>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherRetryTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherIdleSignalTests.cs</tests>
public sealed partial class BatchFlusher<T> : IAsyncDisposable, IQueuedWriteFlusher {
  // What is offered and what is pending, in one word so one atomic read gives both (#1899): the high 32 bits advance by
  // one per offer (the version, wrapping), the low 32 bits hold the pending count. An offer adds OFFER, so the version
  // and the count move together; landing or giving up n items subtracts n. The channel's capacity keeps the count far
  // below 2^31, so it never borrows from the version.
  private const long OFFER = (1L << 32) + 1;
  private const long PENDING_MASK = 0xFFFF_FFFFL;
  private long _state;
  private readonly Lock _idleLock = new();
  // Completed while nothing is pending; replaced by an incomplete one when the count leaves zero.
  private TaskCompletionSource _idle = _completedIdle();
  private readonly CountingWriter _writer;

  private readonly Channel<T> _channel;
  private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _flush;
  private readonly BatchFlusherOptions _options;
  private readonly ILogger _logger;
  private readonly TimeProvider _timeProvider;
  private readonly CancellationTokenSource _stop = new();
  private readonly Lock _startLock = new();
  // Null until the loop starts; set under _startLock, and only once.
  private Task? _loop;
  private bool _startClosed;
  private readonly TaskCompletionSource _stoppedSignal = new();

  /// <summary>Total items consumed across all flushes (observability).</summary>
  public long ItemsFlushed { get; private set; }

  /// <summary>Total flush calls invoked (observability).</summary>
  public long FlushCallCount { get; private set; }

  /// <summary>Items dropped after <see cref="BatchFlusherOptions.MaxFlushAttempts"/> consecutive failed flushes (diagnostic).</summary>
  public long ItemsDropped { get; private set; }

  /// <summary>
  /// Producer-side writer for callers to enqueue items. Every item written here is counted in <see cref="Pending"/>
  /// exactly as one given to <see cref="EnqueueAsync"/> is (#1899): there is no way in that the idle signal cannot see.
  /// </summary>
  public ChannelWriter<T> Writer => _writer;

  /// <summary>Enqueues one item and counts it toward <see cref="Pending"/> until it is flushed or given up.</summary>
  /// <remarks>An item the channel refuses (a stopped flusher, or a write canceled while the channel is full) is not counted.</remarks>
  public ValueTask EnqueueAsync(T item, CancellationToken cancellationToken = default) {
    _offer();
    var write = _channel.Writer.WriteAsync(item, cancellationToken);
    return write.IsCompletedSuccessfully ? write : _awaitOfferedWriteAsync(write);
  }

  private async ValueTask _awaitOfferedWriteAsync(ValueTask write) {
    try {
      await write.ConfigureAwait(false);
    } catch {
      // Refused: never queued, so nothing will flush it. The caller sees the refusal as before.
      _release(1);
      throw;
    }
  }

  /// <summary>
  /// Items offered through <see cref="EnqueueAsync"/> or <see cref="Writer"/> that have neither landed nor been given up:
  /// what waits in the channel plus what the loop has taken up and is flushing. Counted on the producer side so a
  /// reading never misses an item the loop has read but not yet flushed.
  /// </summary>
  public long Pending => Interlocked.Read(ref _state) & PENDING_MASK;

  /// <inheritdoc />
  /// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
  public QueuedWriteActivity Activity {
    get {
      var state = Interlocked.Read(ref _state);
      return new QueuedWriteActivity(Version: (long)((ulong)state >> 32), Pending: state & PENDING_MASK);
    }
  }

  /// <inheritdoc />
  /// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
  public bool IsIdle => Pending == 0;

  /// <inheritdoc />
  /// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
  public event WorkProcessingIdleHandler? OnWorkProcessingIdle;

  /// <inheritdoc />
  /// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
  public Task WaitForIdleAsync(CancellationToken cancellationToken = default) {
    Task idle;
    lock (_idleLock) {
      idle = _idle.Task;
    }
    return idle.WaitAsync(cancellationToken);
  }

  private void _offer() {
    if ((Interlocked.Add(ref _state, OFFER) & PENDING_MASK) == 1) {
      ReconcileIdle();
    }
  }

  private void _release(long count) {
    if ((Interlocked.Add(ref _state, -count) & PENDING_MASK) == 0) {
      ReconcileIdle();
    }
  }

  /// <summary>
  /// Brings the idle awaitable in line with the pending count, and raises the idle event when the count has reached
  /// zero. Called whenever the count leaves or reaches zero; it reads the count afresh under the lock, so whichever of
  /// two crossing calls runs last leaves the state matching the count, and a call that finds nothing to change does
  /// nothing.
  /// </summary>
  internal void ReconcileIdle() {
    WorkProcessingIdleHandler? raise = null;
    lock (_idleLock) {
      var idle = Pending == 0;
      if (idle == _idle.Task.IsCompleted) {
        return;
      }
      if (idle) {
        _idle.SetResult();
        raise = OnWorkProcessingIdle;
      } else {
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      }
    }
    raise?.Invoke();
  }

  private static TaskCompletionSource _completedIdle() {
    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    idle.SetResult();
    return idle;
  }

  /// <summary>Gives up whatever is still in the channel once the loop will read no more: it will never be flushed.</summary>
  private void _abandonQueued() {
    var abandoned = 0;
    while (_channel.Reader.TryRead(out _)) {
      abandoned++;
    }
    if (abandoned > 0) {
      _release(abandoned);
    }
  }

  /// <summary>The writer behind <see cref="Writer"/>: the flusher's channel, with every offer counted.</summary>
  private sealed class CountingWriter(BatchFlusher<T> owner) : ChannelWriter<T> {
    public override bool TryWrite(T item) {
      owner._offer();
      if (owner._channel.Writer.TryWrite(item)) {
        return true;
      }
      owner._release(1);
      return false;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
      owner._channel.Writer.WaitToWriteAsync(cancellationToken);

    public override ValueTask WriteAsync(T item, CancellationToken cancellationToken = default) =>
      owner.EnqueueAsync(item, cancellationToken);

    public override bool TryComplete(Exception? error = null) => owner._channel.Writer.TryComplete(error);
  }

  /// <summary>
  /// Creates the flusher and starts the background coalescing loop.
  /// </summary>
  /// <param name="flush">Callback invoked per flush with the coalesced batch.</param>
  /// <param name="options">Tuning knobs.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="timeProvider">
  /// The clock the coalesce window, the retry backoff and the shutdown drain bound run on. Null uses
  /// <see cref="TimeProvider.System"/>; the flush workers pass the host's registered clock, and a test passes a fake
  /// one so a window or a backoff ends only when the test moves time.
  /// </param>
  /// <docs>operations/configuration/configuration-reference</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherTests.cs:SingleItem_FlushesAfterCoalesceWindowAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherTests.cs:Constructor_WithoutAClock_RunsOnTheSystemClockAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherRetryTests.cs:FlushAlwaysFails_DropsAfterMaxAttemptsAndNamesTheConsequenceAsync</tests>
  public BatchFlusher(
    Func<IReadOnlyList<T>, CancellationToken, Task> flush,
    BatchFlusherOptions options,
    ILogger logger,
    TimeProvider? timeProvider = null) : this(flush, options, logger, timeProvider, started: true) {
  }

  /// <summary>
  /// Creates the flusher, starting its loop now or leaving it for <see cref="Start"/> (#1636): a flush worker's flusher
  /// starts when the host starts the worker, so nothing is flushed before the worker's start point. Items enqueued
  /// before then wait in the channel.
  /// </summary>
  /// <param name="flush">Callback invoked per flush with the coalesced batch.</param>
  /// <param name="options">Tuning knobs.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="timeProvider">The clock; null uses <see cref="TimeProvider.System"/>.</param>
  /// <param name="started">Whether the loop starts now; false leaves it for <see cref="Start"/>.</param>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherDeferredStartTests.cs</tests>
  internal BatchFlusher(
    Func<IReadOnlyList<T>, CancellationToken, Task> flush,
    BatchFlusherOptions options,
    ILogger logger,
    TimeProvider? timeProvider,
    bool started) {
    ArgumentNullException.ThrowIfNull(flush);
    _flush = flush;
    ArgumentNullException.ThrowIfNull(options);
    _options = options;
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _timeProvider = timeProvider ?? TimeProvider.System;

    _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.ChannelCapacity) {
      FullMode = BoundedChannelFullMode.Wait,
      SingleReader = true,
      SingleWriter = false,
    });
    _writer = new CountingWriter(this);

    if (started) {
      Start();
    }
  }

  /// <summary>
  /// Starts the coalescing loop, once; a second call, or a call on a flusher started at construction, changes nothing.
  /// </summary>
  /// <remarks>
  /// The loop runs synchronously up to its first <c>await</c> (the channel read), so the flusher is running by the time
  /// this returns. That avoids Task.Run's thread-pool queueing delay under heavy contention (full-suite test runs at
  /// parallelism 10 saturated the pool and starved Task.Run-launched loops for more than 30 s).
  /// </remarks>
  internal void Start() {
    lock (_startLock) {
      if (!_startClosed) {
        _startClosed = true;
        _loop = _runAsync(_stop.Token);
      }
    }
  }

  private async Task _runAsync(CancellationToken ct) {
    try {
      while (!ct.IsCancellationRequested) {
        var (ok, first) = await BatchFlusherChannelRead.TryReadNextAsync(_channel.Reader, ct).ConfigureAwait(false);
        if (!ok) {
          break;
        }

        var batch = await _coalesceAsync(first).ConfigureAwait(false);

        try {
          await _flushWithRetryAsync(batch, ct);
        } catch (OperationCanceledException) {
          // Shutdown gave up on this batch past the drain bound: it will not land.
          _release(batch.Count);
          break;
        }
        // Landed, or dropped after its last attempt: either way nothing is still coming for these items.
        _release(batch.Count);
      }
    } finally {
      _abandonQueued();
      _stoppedSignal.TrySetResult();
    }
  }

  /// <summary>
  /// Gathers the batch that starts with <paramref name="first"/>: more items join it until it reaches
  /// <see cref="BatchFlusherOptions.MaxBatchSize"/> or <see cref="BatchFlusherOptions.ImmediateFlushThreshold"/>, the
  /// coalesce window closes, or the channel is completed. The window is a timer on the flusher's clock, and the wait
  /// for the next item is a real wait on the channel, not a spin.
  /// </summary>
  /// <remarks>
  /// A batch that already reached its threshold, the first item alone included, flushes without opening a window.
  /// A completed channel ends the wait at once: shutdown drains what is queued instead of sitting out each window.
  /// </remarks>
  private async Task<List<T>> _coalesceAsync(T first) {
    var batch = new List<T>(_options.MaxBatchSize) { first };
    var target = Math.Min(_options.MaxBatchSize, _options.ImmediateFlushThreshold);
    if (batch.Count >= target || _options.CoalesceWindowMs <= 0) {
      return batch;
    }

    using var window = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.CoalesceWindowMs), _timeProvider);
    while (batch.Count < target) {
      // Completes at once when an item is already waiting. The window closing cancels the wait; that is observed as a
      // status rather than thrown, since it is how most windows end.
      var more = _channel.Reader.WaitToReadAsync(window.Token).AsTask();
      await ((Task)more).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      if (!more.IsCompletedSuccessfully || !more.Result) {
        break;
      }
      while (batch.Count < target && _channel.Reader.TryRead(out var next)) {
        batch.Add(next);
      }
    }
    return batch;
  }

  /// <summary>
  /// A failed flush is retried in place with a backoff instead of being discarded. The items are
  /// completions, lease renewals and failures: losing them leaves rows leased until expiry and
  /// re-claimed afterwards, which is how one transient timeout turned into lease churn under a bulk
  /// import. The flush is idempotent by contract, so retrying after a partial success is safe. Only
  /// after <see cref="BatchFlusherOptions.MaxFlushAttempts"/> consecutive failures is the batch
  /// dropped, at Error, with the consequence named.
  /// </summary>
  private async Task _flushWithRetryAsync(List<T> batch, CancellationToken ct) {
    var maxAttempts = Math.Max(1, _options.MaxFlushAttempts);
    var attempts = 0;
    while (true) {
      attempts++;
      try {
        await _flush(batch, ct);
        FlushCallCount++;
        ItemsFlushed += batch.Count;
        return;
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        if (attempts >= maxAttempts) {
          ItemsDropped += batch.Count;
          LogBatchDropped(_logger, batch.Count, attempts, ex);
          return;
        }
        var backoffMs = Math.Min(
          Math.Max(1, _options.FlushRetryBackoffMs) * (1 << Math.Min(attempts - 1, 6)),
          Math.Max(1, _options.FlushRetryMaxBackoffMs));
        LogFlushRetry(_logger, batch.Count, attempts, maxAttempts, backoffMs, ex);
        await Task.Delay(TimeSpan.FromMilliseconds(backoffMs), _timeProvider, ct);
      }
    }
  }

  private int _disposed;

  /// <inheritdoc />
  /// <remarks>
  /// Idempotent: host teardown reaches the flusher through more than one path (the owning
  /// worker's StopAsync and container disposal), and a second call must not cancel the
  /// already-disposed stop CTS.
  /// </remarks>
  public async ValueTask DisposeAsync() {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      await _stoppedSignal.Task.ConfigureAwait(false);
      return;
    }
    // A flusher whose worker never started (#1636) flushes nothing, not even on the way out: claiming the start here
    // keeps a late Start from running a loop after this, and what was queued is dropped with the channel.
    Task? loop;
    lock (_startLock) {
      _startClosed = true;
      loop = _loop;
    }
    if (loop is null) {
      _channel.Writer.TryComplete();
      _abandonQueued();
      _stoppedSignal.TrySetResult();
      _stop.Dispose();
      return;
    }

    // Completing the writer is what lets the loop DRAIN: ReadAsync throws ChannelClosedException
    // only once the channel is both completed and empty, so the loop finishes the work already
    // queued and then exits on its own. Cancelling first -- as this used to -- makes ReadAsync
    // throw immediately and silently discards whatever had not been read yet. Five workers share
    // this flusher (lease renewals, inbox handler commits, perspective completions, outbox
    // completions, message failures), so that discard showed up as expired leases, reprocessed
    // messages, stalled cursors and messages stuck in-flight, once per graceful shutdown.
    _channel.Writer.TryComplete();
    try {
      // CancellationToken.None is deliberate and load-bearing: _stop is what this method cancels
      // *after* the drain gives up, so flowing it in here would let an external cancel abort the
      // drain early and discard the buffered work this whole path exists to flush.
      await loop
        .WaitAsync(TimeSpan.FromMilliseconds(_options.DrainTimeoutMs), _timeProvider, CancellationToken.None)
        .ConfigureAwait(false);
    } catch (TimeoutException) {
      // A flush callback that will not return must not hold host shutdown open forever.
      LogDrainTimeout(_logger, _options.DrainTimeoutMs);
      await _stop.CancelAsync().ConfigureAwait(false);
      try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { /* stopping is the normal way out of this wait */ }
    }

    if (!_stop.IsCancellationRequested) {
      await _stop.CancelAsync().ConfigureAwait(false);
    }
    _stop.Dispose();
  }

  /// <summary>Test/diagnostic hook: completes when the loop has exited.</summary>
  public Task StoppedSignal => _stoppedSignal.Task;

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "BatchFlusher drain exceeded {DrainTimeoutMs}ms during shutdown; remaining items dropped")]
  static partial void LogDrainTimeout(ILogger logger, int drainTimeoutMs);

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "BatchFlusher flush failed for batch of {Count} (attempt {Attempt} of {MaxAttempts}); retrying the same batch in {BackoffMs}ms")]
  static partial void LogFlushRetry(ILogger logger, int count, int attempt, int maxAttempts, int backoffMs, Exception ex);

  [LoggerMessage(EventId = 3, Level = LogLevel.Error,
    Message = "BatchFlusher dropped a batch of {Count} after {Attempts} failed flushes; the rows behind these items stay leased until their lease expires and are then re-claimed and redone (the flush is idempotent)")]
  static partial void LogBatchDropped(ILogger logger, int count, int attempts, Exception ex);
}

/// <summary>Configuration for <see cref="BatchFlusher{T}"/>.</summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public sealed class BatchFlusherOptions {
  /// <summary>Bounded channel capacity (back-pressure when full). Default 10 000.</summary>
  public int ChannelCapacity { get; set; } = 10_000;

  /// <summary>Maximum items per flush call. Default 500.</summary>
  public int MaxBatchSize { get; set; } = 500;

  /// <summary>Maximum wait (ms) coalescing additional items after the first. Default 25.</summary>
  public int CoalesceWindowMs { get; set; } = 25;

  /// <summary>
  /// If the batch reaches this size before the window closes, flush immediately. Default 250. The first item counts:
  /// a threshold of one flushes every item as it arrives, without opening a coalesce window.
  /// </summary>
  public int ImmediateFlushThreshold { get; set; } = 250;

  /// <summary>
  /// How long shutdown waits for queued items to drain before giving up and cancelling.
  /// Bounds the case where a flush callback never returns; reaching it drops the remainder
  /// and logs a warning, which is strictly better than blocking host shutdown indefinitely.
  /// </summary>
  public int DrainTimeoutMs { get; set; } = 5_000;

  /// <summary>Consecutive failed flushes of one batch before it is dropped. Default 5.</summary>
  public int MaxFlushAttempts { get; set; } = 5;

  /// <summary>Backoff before the first retry of a failed flush, doubled per attempt. Default 250 ms.</summary>
  public int FlushRetryBackoffMs { get; set; } = 250;

  /// <summary>Cap on the retry backoff. Default 5000 ms.</summary>
  public int FlushRetryMaxBackoffMs { get; set; } = 5_000;
}

/// <summary>
/// The read at the head of a <see cref="BatchFlusher{T}"/> loop, and the one decision it makes:
/// whether there is another item or the loop is over.
/// </summary>
/// <remarks>
/// Split out and internal because the two ways a read ends the loop cannot both be produced
/// through <c>DisposeAsync</c>: it always completes the writer first, so a pending read resolves
/// as a closed channel long before the stop token is ever canceled, and the canceled answer —
/// which a forced shutdown past the drain timeout does produce — has no ordering a test can pin.
/// Both still have to end the loop quietly: five workers share one flusher, and a read that threw
/// out of the loop would fault a task nobody awaits and silently stop every one of them.
/// </remarks>
internal static class BatchFlusherChannelRead {
  /// <summary>
  /// Reads the next item. <c>Ok</c> is false when the loop should stop — the channel is closed
  /// and drained, or the stop token fired while the read was pending.
  /// </summary>
  internal static async Task<(bool Ok, T Item)> TryReadNextAsync<T>(ChannelReader<T> reader, CancellationToken ct) {
    try {
      return (true, await reader.ReadAsync(ct).ConfigureAwait(false));
    } catch (OperationCanceledException) {
      return (false, default!);
    } catch (ChannelClosedException) {
      return (false, default!);
    }
  }
}
