using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Default <see cref="IApplyBatchStrategy"/> — per-stream-keyed sliding-window batcher.
/// Each stream_id has its own bounded channel + drain task. Same-stream drain signals
/// coalesce through one buffer; different streams batch independently in parallel
/// windows. Mirrors <see cref="SlidingWindowOutboxBatchStrategy"/> in shape (slice 9 of
/// pump-then-process) but applied at the perspective apply boundary.
/// </summary>
/// <remarks>
/// <para>Slice 22c. Defaults baked into <see cref="SlidingWindowApplyOptions"/>:
/// 300 ms debounce / 3 s hard cap / 1000-signal ceiling per stream / 30 s idle eviction.</para>
/// <para>
/// Motivation: <c>UberDraftJob</c>-style aggregate perspectives receive ~45 events per
/// stream during JDX bulk import. Without batching at the apply boundary, each drain
/// signal triggers a separate apply cycle (read snapshot → apply 1 event → atomic upsert)
/// even though all events are already pending. With the window, signals coalesce into
/// one flush per stream, producing one snapshot read + one apply pass + one atomic upsert.
/// </para>
/// <para>
/// Memory bound via idle eviction: streams with no signals for
/// <see cref="SlidingWindowApplyOptions.IdleEvictionWindow"/> get their buffer disposed.
/// The next signal for that stream creates a fresh buffer.
/// </para>
/// </remarks>
/// <docs>internals/apply-batch-strategy</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/SlidingWindowApplyBatchStrategyTests.cs</tests>
public sealed class SlidingWindowApplyBatchStrategy : IApplyBatchStrategy {
  private readonly ApplyBulkFlushCallback _flush;
  private readonly SlidingWindowApplyOptions _options;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger? _logger;

  private readonly ConcurrentDictionary<Guid, StreamBuffer> _streams = new();
  private readonly CancellationTokenSource _stopCts = new();
  private readonly ITimer _idleSweepTimer;
  private int _disposed;

  /// <summary>
  /// Creates the strategy with the given flush callback.
  /// </summary>
  /// <param name="flush">Called once per per-stream sliding-window flush. The signal count is informational; the downstream apply pipeline fetches actual pending events for the stream.</param>
  /// <param name="options">Tuning knobs; null uses 300 ms / 3 s / 1000 defaults.</param>
  /// <param name="timeProvider">Time source. Pass <see cref="TimeProvider.System"/> in production, fake in tests.</param>
  /// <param name="logger">Optional logger; flush exceptions get logged at Error.</param>
  public SlidingWindowApplyBatchStrategy(
      ApplyBulkFlushCallback flush,
      SlidingWindowApplyOptions? options = null,
      TimeProvider? timeProvider = null,
      ILogger<SlidingWindowApplyBatchStrategy>? logger = null) {
    ArgumentNullException.ThrowIfNull(flush);
    _flush = flush;
    _options = options ?? new SlidingWindowApplyOptions();
    _timeProvider = timeProvider ?? TimeProvider.System;
    _logger = logger;

    _idleSweepTimer = _timeProvider.CreateTimer(
      _ => _ = _runIdleSweepAsync(),
      state: null,
      dueTime: _options.IdleSweepInterval,
      period: _options.IdleSweepInterval);
  }

  /// <summary>Active per-stream buffer count — exposed for tests + diagnostics.</summary>
  public int ActiveStreamCount => _streams.Count;

  /// <inheritdoc />
  public async ValueTask AppendAsync(Guid streamId, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    var buffer = _streams.GetOrAdd(streamId, k => _createStreamBuffer(k));
    buffer.LastActivity = _timeProvider.GetUtcNow();
    await buffer.Writer.WriteAsync(streamId, cancellationToken).ConfigureAwait(false);
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
    } catch (OperationCanceledException) {
      _stopCts.Cancel();
    }
    _stopCts.Dispose();
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    await FlushAndStopAsync().ConfigureAwait(false);
  }

  private StreamBuffer _createStreamBuffer(Guid key) {
    var channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(_options.MaxSize * 4) {
      SingleReader = true,
      SingleWriter = false,
      FullMode = BoundedChannelFullMode.Wait,
    });
    var batcherOptions = new SlidingWindowBatcherOptions {
      SlidingWindow = _options.SlidingWindow,
      MaxWait = _options.MaxWait,
      MaxSize = _options.MaxSize,
    };
    var batcher = new SlidingWindowBatcher<Guid>(channel.Reader, batcherOptions, _timeProvider);
    var buffer = new StreamBuffer(key, channel, _timeProvider.GetUtcNow());
    buffer.Worker = Task.Run(() => _drainBufferAsync(buffer, batcher));
    return buffer;
  }

  private async Task _drainBufferAsync(StreamBuffer buffer, SlidingWindowBatcher<Guid> batcher) {
    try {
      await foreach (var batch in batcher.ReadBatchesAsync(_stopCts.Token).ConfigureAwait(false)) {
        if (batch.Count == 0) {
          continue;
        }
        // All signals in this batch are for the same stream (per-stream-keyed buffer).
        // Drain signals dedupe semantically — one apply cycle picks up everything pending
        // for the stream — so we only need to fire the callback ONCE per batch with the
        // stream_id and the count (informational).
        try {
          await _flush(buffer.Key, batch.Count, _stopCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_stopCts.IsCancellationRequested) {
          return;
        } catch (Exception ex) {
          _logFlushFailed(ex, buffer.Key, batch.Count);
          // Drop the batch on error — the perspective worker's own retry path (slice 19
          // bounded retry) and the claim_orphaned_perspective_events SQL reclaim handle
          // re-issuing work that didn't complete. The strategy is fire-and-forget at this
          // boundary; durability lives in wh_perspective_events + the lease system.
        }
      }
    } catch (OperationCanceledException) {
      // shutdown
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

  private void _logFlushFailed(Exception ex, Guid streamKey, int signalCount) {
#pragma warning disable CA1848
    _logger?.LogError(ex,
      "SlidingWindowApplyBatchStrategy: flush of {SignalCount} signal(s) for stream {StreamKey} failed. claim_orphaned_perspective_events will recover.",
      signalCount, streamKey);
#pragma warning restore CA1848
  }

  private sealed class StreamBuffer(Guid key, Channel<Guid> channel, DateTimeOffset createdAt) {
    public Guid Key { get; } = key;
    public ChannelReader<Guid> Reader => channel.Reader;
    public ChannelWriter<Guid> Writer => channel.Writer;
    public DateTimeOffset LastActivity { get; set; } = createdAt;
    public Task Worker { get; set; } = Task.CompletedTask;
  }
}
