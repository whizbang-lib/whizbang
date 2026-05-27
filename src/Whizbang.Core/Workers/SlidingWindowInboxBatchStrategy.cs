using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

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
/// MessageId order on fan-in streams. The result was 5,622 cursor inversions per a consumer
/// bulk-import run, mostly on BulkImportOrchestration / Order / saga streams.
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
/// </remarks>
/// <docs>extending/internals/event-ordering-invariant</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/SlidingWindowInboxBatchStrategyTests.cs</tests>
public sealed class SlidingWindowInboxBatchStrategy : IInboxBatchStrategy {
  private static readonly Guid _defaultStreamKey = Guid.Empty;

  private readonly InboxBulkFlushCallback _flush;
  private readonly SlidingWindowInboxOptions _options;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger? _logger;

  private readonly ConcurrentDictionary<Guid, StreamBuffer> _streams = new();
  private readonly CancellationTokenSource _stopCts = new();
  private readonly ITimer _idleSweepTimer;
  private int _disposed;

  /// <summary>
  /// Creates the strategy with the given flush callback and (optionally) tuned options + clock.
  /// </summary>
  /// <param name="flush">Called with each per-stream batch. Typically resolves <see cref="IWorkCoordinator"/> from a DI scope and calls <see cref="IWorkCoordinator.StoreInboxMessagesAsync"/>.</param>
  /// <param name="options">Tuning knobs; null uses the 300 ms / 3 s / 1000 defaults.</param>
  /// <param name="timeProvider">Time source. Pass <see cref="TimeProvider.System"/> in production, fake in tests.</param>
  /// <param name="logger">Optional logger; flush exceptions get logged at Error.</param>
  public SlidingWindowInboxBatchStrategy(
      InboxBulkFlushCallback flush,
      SlidingWindowInboxOptions? options = null,
      TimeProvider? timeProvider = null,
      ILogger<SlidingWindowInboxBatchStrategy>? logger = null) {
    ArgumentNullException.ThrowIfNull(flush);
    _flush = flush;
    _options = options ?? new SlidingWindowInboxOptions();
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
  public async ValueTask AppendAsync(InboxMessage message, CancellationToken cancellationToken = default) {
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
    await FlushAndStopAsync().ConfigureAwait(false);
  }

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
    buffer.Worker = Task.Run(() => _drainBufferAsync(buffer, batcher));
    return buffer;
  }

  private async Task _drainBufferAsync(StreamBuffer buffer, SlidingWindowBatcher<InboxMessage> batcher) {
    try {
      await foreach (var batch in batcher.ReadBatchesAsync(_stopCts.Token).ConfigureAwait(false)) {
        if (batch.Count == 0) {
          continue;
        }
        // Slice 18b: sort by MessageId (UUIDv7 chronological) before flushing. With the
        // slice-23 per-stream buffer this sort now covers cross-transport-message events
        // for the same stream — fan-in saga aggregates no longer see late events because
        // the window holds them until quiet, then flushes them all in MessageId order.
        var array = batch is InboxMessage[] arr ? arr : [.. batch];
        if (array.Length > 1) {
          Array.Sort(array, static (a, b) => a.MessageId.CompareTo(b.MessageId));
        }
        try {
          await _flush(array, _stopCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_stopCts.IsCancellationRequested) {
          return;
        } catch (Exception ex) {
          _logFlushFailed(ex, buffer.Key, array.Length);
          // Drop the batch on error — caller is responsible for retry policy. Errors here
          // are exceptional (DB unavailable / serialization issue). Inbox writes are
          // idempotent via wh_message_deduplication so re-delivery from the transport
          // replays cleanly.
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
      "SlidingWindowInboxBatchStrategy: bulk flush of {BatchSize} message(s) for stream {StreamKey} failed. Transport redelivery will recover.",
      batchSize, streamKey);
#pragma warning restore CA1848
  }

  private sealed class StreamBuffer(Guid key, Channel<InboxMessage> channel, DateTimeOffset createdAt) {
    public Guid Key { get; } = key;
    public ChannelReader<InboxMessage> Reader => channel.Reader;
    public ChannelWriter<InboxMessage> Writer => channel.Writer;
    public DateTimeOffset LastActivity { get; set; } = createdAt;
    public Task Worker { get; set; } = Task.CompletedTask;
  }
}
