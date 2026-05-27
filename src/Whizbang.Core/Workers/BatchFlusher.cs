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
public sealed partial class BatchFlusher<T> : IAsyncDisposable {
  private readonly Channel<T> _channel;
  private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _flush;
  private readonly BatchFlusherOptions _options;
  private readonly ILogger _logger;
  private readonly CancellationTokenSource _stop = new();
  private readonly Task _loop;
  private readonly TaskCompletionSource _stoppedSignal = new();

  /// <summary>Total items consumed across all flushes (observability).</summary>
  public long ItemsFlushed { get; private set; }

  /// <summary>Total flush calls invoked (observability).</summary>
  public long FlushCallCount { get; private set; }

  /// <summary>Producer-side writer for callers to enqueue items.</summary>
  public ChannelWriter<T> Writer => _channel.Writer;

  /// <summary>
  /// Creates the flusher and starts the background coalescing loop.
  /// </summary>
  /// <param name="flush">Callback invoked per flush with the coalesced batch.</param>
  /// <param name="options">Tuning knobs.</param>
  /// <param name="logger">Logger.</param>
  public BatchFlusher(
    Func<IReadOnlyList<T>, CancellationToken, Task> flush,
    BatchFlusherOptions options,
    ILogger logger) {
    _flush = flush ?? throw new ArgumentNullException(nameof(flush));
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        T first;
        try {
          first = await _channel.Reader.ReadAsync(ct);
        } catch (OperationCanceledException) {
          break;
        } catch (ChannelClosedException) {
          break;
        }

        var batch = new List<T>(_options.MaxBatchSize) { first };
        var deadlineMs = Environment.TickCount + _options.CoalesceWindowMs;

        // Coalesce additional items up to MaxBatchSize OR until deadline OR ImmediateFlushThreshold.
        while (batch.Count < _options.MaxBatchSize && Environment.TickCount < deadlineMs) {
          if (_channel.Reader.TryRead(out var next)) {
            batch.Add(next);
            if (batch.Count >= _options.ImmediateFlushThreshold) {
              break;
            }
          } else {
            // No item ready right now; yield (avoid busy spin) then re-check until deadline.
            await Task.Yield();
          }
        }

        try {
          await _flush(batch, ct);
          FlushCallCount++;
          ItemsFlushed += batch.Count;
        } catch (OperationCanceledException) {
          break;
        } catch (Exception ex) {
          LogFlushError(_logger, batch.Count, ex);
          // Items are lost on flush failure — caller's responsibility to make the flush idempotent.
        }
      }
    } finally {
      _stoppedSignal.TrySetResult();
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    _channel.Writer.TryComplete();
    await _stop.CancelAsync();
    try { await _loop; } catch (OperationCanceledException) { }
    _stop.Dispose();
  }

  /// <summary>Test/diagnostic hook: completes when the loop has exited.</summary>
  public Task StoppedSignal => _stoppedSignal.Task;

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "BatchFlusher flush failed for batch of {Count}; items lost (caller flush should be idempotent)")]
  static partial void LogFlushError(ILogger logger, int count, Exception ex);
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

  /// <summary>If batch reaches this size before the window closes, flush immediately. Default 250.</summary>
  public int ImmediateFlushThreshold { get; set; } = 250;
}
