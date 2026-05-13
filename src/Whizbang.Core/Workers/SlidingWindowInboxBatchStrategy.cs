using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Default <see cref="IInboxBatchStrategy"/> — wraps <see cref="SlidingWindowBatcher{T}"/>
/// over an unbounded channel of <see cref="InboxMessage"/>. Producers (the receive boundary)
/// call <see cref="AppendAsync"/>; the batcher coalesces them into windows and invokes the
/// configured <see cref="InboxBulkFlushCallback"/> with each batch.
/// </summary>
/// <remarks>
/// <para>Slice 2 of plans/pump-then-process.md. Defaults baked in
/// <see cref="SlidingWindowInboxOptions"/>: 50 ms / 1 s / 100. Consumers can override via DI.</para>
/// <para>A single drain task reads batches from the underlying batcher and invokes the flush
/// callback. The drain task lives until <see cref="FlushAndStopAsync"/> completes the channel
/// or the strategy is disposed; pending messages drain out before the task exits.</para>
/// </remarks>
/// <docs>extending/internals/event-ordering-invariant</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/SlidingWindowInboxBatchStrategyTests.cs</tests>
public sealed class SlidingWindowInboxBatchStrategy : IInboxBatchStrategy {
  private readonly Channel<InboxMessage> _channel;
  private readonly Task _drainLoop;
  private readonly InboxBulkFlushCallback _flush;
  private readonly ILogger? _logger;
  private readonly CancellationTokenSource _stopCts = new();
  private int _disposed;

  /// <summary>
  /// Creates the strategy with the given flush callback and (optionally) tuned options + clock.
  /// </summary>
  /// <param name="flush">Called with each batch. Typically resolves <see cref="IWorkCoordinator"/> from a DI scope and calls <see cref="IWorkCoordinator.StoreInboxMessagesAsync"/>.</param>
  /// <param name="options">Tuning knobs; null uses 50 ms / 1 s / 100 defaults.</param>
  /// <param name="timeProvider">Time source. Pass <see cref="TimeProvider.System"/> in production, fake in tests.</param>
  /// <param name="logger">Optional logger; flush exceptions get logged at Error.</param>
  public SlidingWindowInboxBatchStrategy(
      InboxBulkFlushCallback flush,
      SlidingWindowInboxOptions? options = null,
      TimeProvider? timeProvider = null,
      ILogger<SlidingWindowInboxBatchStrategy>? logger = null) {
    ArgumentNullException.ThrowIfNull(flush);
    _flush = flush;
    _logger = logger;

    options ??= new SlidingWindowInboxOptions();
    var batcherOptions = new SlidingWindowBatcherOptions {
      SlidingWindow = options.SlidingWindow,
      MaxWait = options.MaxWait,
      MaxSize = options.MaxSize,
    };

    _channel = Channel.CreateUnbounded<InboxMessage>(new UnboundedChannelOptions {
      SingleReader = true,
      SingleWriter = false,
    });
    var batcher = new SlidingWindowBatcher<InboxMessage>(_channel.Reader, batcherOptions, timeProvider);
    _drainLoop = Task.Run(() => _drainAsync(batcher), _stopCts.Token);
  }

  /// <inheritdoc />
  public ValueTask AppendAsync(InboxMessage message, CancellationToken cancellationToken = default)
    => _channel.Writer.WriteAsync(message, cancellationToken);

  /// <inheritdoc />
  public async Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }
    _channel.Writer.TryComplete();
    try {
      await _drainLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      _stopCts.Cancel();
    }
    _stopCts.Dispose();
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    await FlushAndStopAsync().ConfigureAwait(false);
  }

  private async Task _drainAsync(SlidingWindowBatcher<InboxMessage> batcher) {
    try {
      await foreach (var batch in batcher.ReadBatchesAsync(_stopCts.Token).ConfigureAwait(false)) {
        if (batch.Count == 0) {
          continue;
        }
        var array = batch is InboxMessage[] arr ? arr : [.. batch];
        // Slice 18: sort by MessageId (UUIDv7 chronological) before flushing. Concurrent
        // transport consumers can deposit messages into this channel in non-deterministic
        // order; sorting here locks the every-batch-boundary-delivers-sorted-output
        // invariant that the perspective-apply cursor (event_id lex) depends on.
        if (array.Length > 1) {
          Array.Sort(array, static (a, b) => a.MessageId.CompareTo(b.MessageId));
        }
        try {
          await _flush(array, _stopCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_stopCts.IsCancellationRequested) {
          return;
        } catch (Exception ex) {
          _logFlushFailed(ex, array.Length);
          // Drop the batch on error — caller is responsible for retry policy. Errors here are
          // exceptional (DB unavailable / serialization issue). Inbox writes are idempotent
          // via wh_message_deduplication so re-delivery from the transport replays cleanly.
        }
      }
    } catch (OperationCanceledException) {
      // shutdown
    }
  }

  private void _logFlushFailed(Exception ex, int batchSize) {
#pragma warning disable CA1848
    _logger?.LogError(ex,
      "SlidingWindowInboxBatchStrategy: bulk flush of {BatchSize} message(s) failed. Transport redelivery will recover.",
      batchSize);
#pragma warning restore CA1848
  }
}
