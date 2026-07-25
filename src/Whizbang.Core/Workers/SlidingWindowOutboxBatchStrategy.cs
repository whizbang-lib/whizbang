using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

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
/// </remarks>
/// <docs>extending/internals/event-ordering-invariant</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/SlidingWindowOutboxBatchStrategyTests.cs</tests>
public sealed class SlidingWindowOutboxBatchStrategy : IOutboxBatchStrategy {
  private static readonly Guid _defaultStreamKey = Guid.Empty;

  private readonly OutboxBulkFlushCallback _flush;
  private readonly SlidingWindowOutboxOptions _options;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger? _logger;

  private readonly ConcurrentDictionary<Guid, StreamBuffer> _streams = new();
  private readonly CancellationTokenSource _stopCts = new();
  private readonly ITimer _idleSweepTimer;
  private int _disposed;

  /// <summary>
  /// Creates the strategy with the given flush callback.
  /// </summary>
  /// <param name="flush">Called with each per-stream batch. Typically resolves <see cref="IWorkCoordinator"/> from a DI scope and calls <see cref="IWorkCoordinator.StoreOutboxMessagesAsync"/>.</param>
  /// <param name="options">Tuning knobs; null uses 50 ms / 1 s / 100 defaults.</param>
  /// <param name="timeProvider">Time source. Pass <see cref="TimeProvider.System"/> in production, fake in tests.</param>
  /// <param name="logger">Optional logger; flush exceptions get logged at Error.</param>
  public SlidingWindowOutboxBatchStrategy(
      OutboxBulkFlushCallback flush,
      SlidingWindowOutboxOptions? options = null,
      TimeProvider? timeProvider = null,
      ILogger<SlidingWindowOutboxBatchStrategy>? logger = null) {
    ArgumentNullException.ThrowIfNull(flush);
    _flush = flush;
    _options = options ?? new SlidingWindowOutboxOptions();
    _timeProvider = timeProvider ?? TimeProvider.System;
    _logger = logger;

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
    var buffer = _streams.GetOrAdd(key, k => _createStreamBuffer(k));
    buffer.LastActivity = _timeProvider.GetUtcNow();
    await buffer.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
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
    await FlushAndStopAsync(CancellationToken.None).ConfigureAwait(false);
  }

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
    buffer.Worker = Task.Run(() => _drainBufferAsync(buffer, batcher), _stopCts.Token);
    return buffer;
  }

  private async Task _drainBufferAsync(StreamBuffer buffer, SlidingWindowBatcher<OutboxMessage> batcher) {
    try {
      await foreach (var batch in batcher.ReadBatchesAsync(_stopCts.Token).ConfigureAwait(false)) {
        if (batch.Count == 0) {
          continue;
        }
        // Sort by MessageId — locks the producer-side stream-affinity invariant. Concurrent
        // appends to this stream's channel may complete in non-deterministic order; the sort
        // restores message_id (UUIDv7 chronological) order before the SQL chain call.
        var array = batch.ToArray();
        if (array.Length > 1) {
          Array.Sort(array, static (a, b) => a.MessageId.CompareTo(b.MessageId));
        }
        try {
          await _flush(array, _stopCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_stopCts.IsCancellationRequested) {
          return;
        } catch (Exception ex) {
          _logFlushFailed(ex, buffer.Key, array.Length);
          // Drop the batch on error — caller is responsible for retry policy. Outbox writes
          // are idempotent at the SQL level (ON CONFLICT DO NOTHING in store_outbox_messages),
          // so a re-emit from the dispatcher recovers cleanly.
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

  private void _logFlushFailed(Exception ex, Guid streamKey, int batchSize) {
#pragma warning disable CA1848
    _logger?.LogError(ex,
      "SlidingWindowOutboxBatchStrategy: bulk flush of {BatchSize} message(s) for stream {StreamKey} failed. Dispatcher re-emission will recover.",
      batchSize, streamKey);
#pragma warning restore CA1848
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1854:Unused assignments should be removed", Justification = "Discard pattern is the canonical fire-and-forget idiom for the timer callback; the returned Task is observed via the worker's internal error handling.")]
  private void _fireAndForgetIdleSweep() {
    _ = _runIdleSweepAsync();
  }

  private sealed class StreamBuffer(Guid key, Channel<OutboxMessage> channel, DateTimeOffset createdAt) {
    public Guid Key { get; } = key;
    public ChannelReader<OutboxMessage> Reader => channel.Reader;
    public ChannelWriter<OutboxMessage> Writer => channel.Writer;
    public DateTimeOffset LastActivity { get; set; } = createdAt;
    public Task Worker { get; set; } = Task.CompletedTask;
  }
}
