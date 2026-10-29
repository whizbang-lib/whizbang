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
/// <docs>operations/configuration/configuration-reference</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherRetryTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherTests.cs</tests>
public sealed partial class BatchFlusher<T> : IAsyncDisposable {
  private readonly Channel<T> _channel;
  private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _flush;
  private readonly BatchFlusherOptions _options;
  private readonly ILogger _logger;
  private readonly TimeProvider _timeProvider;
  private readonly CancellationTokenSource _stop = new();
  private readonly Task _loop;
  private readonly TaskCompletionSource _stoppedSignal = new();

  /// <summary>Total items consumed across all flushes (observability).</summary>
  public long ItemsFlushed { get; private set; }

  /// <summary>Total flush calls invoked (observability).</summary>
  public long FlushCallCount { get; private set; }

  /// <summary>Items dropped after <see cref="BatchFlusherOptions.MaxFlushAttempts"/> consecutive failed flushes (diagnostic).</summary>
  public long ItemsDropped { get; private set; }

  /// <summary>Producer-side writer for callers to enqueue items. Items written here are not counted in <see cref="Pending"/>; use <see cref="EnqueueAsync"/> for that.</summary>
  public ChannelWriter<T> Writer => _channel.Writer;

  private long _accepted;

  /// <summary>Enqueues one item and counts it toward <see cref="Pending"/> until it is flushed or dropped.</summary>
  public ValueTask EnqueueAsync(T item, CancellationToken cancellationToken = default) {
    Interlocked.Increment(ref _accepted);
    return _channel.Writer.WriteAsync(item, cancellationToken);
  }

  /// <summary>
  /// Items accepted through <see cref="EnqueueAsync"/> that are neither flushed nor dropped yet: what waits
  /// in the channel plus what the loop has taken up and is flushing. Counted on the producer side so a
  /// reading never misses an item the loop has read but not yet flushed.
  /// </summary>
  public long Pending => Math.Max(0, Interlocked.Read(ref _accepted) - ItemsFlushed - ItemsDropped);

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
    TimeProvider? timeProvider = null) {
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

    // Start the loop synchronously up to its first `await` (channel.ReadAsync) so the
    // flusher is "running" by the time the ctor returns — important when callers race
    // ahead and Writer.WriteAsync immediately. Avoids Task.Run's threadpool-queueing
    // delay under heavy contention (full-suite test runs at parallelism=10 saturated
    // the threadpool and starved Task.Run-launched loops for >30s).
    _loop = _runAsync(_stop.Token);
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
          break;
        }
      }
    } finally {
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
      await _loop
        .WaitAsync(TimeSpan.FromMilliseconds(_options.DrainTimeoutMs), _timeProvider, CancellationToken.None)
        .ConfigureAwait(false);
    } catch (TimeoutException) {
      // A flush callback that will not return must not hold host shutdown open forever.
      LogDrainTimeout(_logger, _options.DrainTimeoutMs);
      await _stop.CancelAsync().ConfigureAwait(false);
      try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { /* stopping is the normal way out of this wait */ }
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
/// <docs>operations/configuration/configuration-reference</docs>
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
