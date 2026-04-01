using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Default <see cref="IInboxBatchStrategy"/> that collects inbox messages from concurrent
/// transport handlers and flushes them together in a single <c>process_work_batch</c> call.
/// </summary>
/// <remarks>
/// <para>
/// Three flush triggers (whichever fires first):
/// <list type="bullet">
///   <item><b>Batch size</b>: <see cref="MessageProcessingOptions.InboxBatchSize"/> messages accumulated → immediate flush</item>
///   <item><b>Sliding window</b>: <see cref="MessageProcessingOptions.InboxBatchSlideMs"/> since last enqueue → flush partial batch</item>
///   <item><b>Hard max</b>: <see cref="MessageProcessingOptions.InboxBatchMaxWaitMs"/> since first message in batch → flush regardless</item>
/// </list>
/// </para>
/// <para>
/// Thread-safe: multiple transport handler threads call <see cref="EnqueueAndWaitAsync"/> concurrently.
/// Each caller blocks until the batch containing its message flushes. All callers in the same batch
/// receive the same <see cref="WorkBatch"/> instance; each filters by <c>MessageId</c> for its own work.
/// </para>
/// <para>
/// On flush, creates ONE DI scope, resolves an <see cref="IWorkCoordinatorStrategy"/>, queues all
/// messages, and calls <see cref="IWorkCoordinatorStrategy.FlushAsync"/> once. This collapses N
/// concurrent <c>process_work_batch</c> calls into 1.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Workers/SlidingWindowInboxBatchStrategyTests.cs</tests>
/// <docs>messaging/transports/transport-consumer#inbox-batching</docs>
public sealed class SlidingWindowInboxBatchStrategy : IInboxBatchStrategy {
  private readonly MessageProcessingOptions _options;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly TransportMetrics? _metrics;

  private readonly Lock _lock = new();
  private List<PendingInbox> _pending = [];
  private Timer? _slideTimer;
  private Timer? _hardMaxTimer;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="SlidingWindowInboxBatchStrategy"/>.
  /// </summary>
  /// <param name="options">Configuration for batch size, slide window, and hard max timers.</param>
  /// <param name="scopeFactory">Scope factory for creating DI scopes during flush.</param>
  /// <param name="metrics">Optional transport metrics for observability.</param>
  public SlidingWindowInboxBatchStrategy(
    MessageProcessingOptions options,
    IServiceScopeFactory scopeFactory,
    TransportMetrics? metrics = null
  ) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(scopeFactory);

    _options = options;
    _scopeFactory = scopeFactory;
    _metrics = metrics;

    _slideTimer = new Timer(_slideTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    _hardMaxTimer = new Timer(_hardMaxTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
  }

  /// <inheritdoc />
  public Task<WorkBatch> EnqueueAndWaitAsync(InboxMessage message, CancellationToken ct) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    var tcs = new TaskCompletionSource<WorkBatch>(TaskCreationOptions.RunContinuationsAsynchronously);

    // Register cancellation callback
    var registration = ct.CanBeCanceled
      ? ct.Register(() => tcs.TrySetCanceled(ct))
      : default;

    bool shouldFlushNow;

    lock (_lock) {
      _pending.Add(new PendingInbox(message, tcs, registration, Stopwatch.GetTimestamp()));

      // Start hard max timer on first message in batch
      if (_pending.Count == 1) {
        _hardMaxTimer?.Change(_options.InboxBatchMaxWaitMs, Timeout.Infinite);
      }

      // Check batch size trigger
      shouldFlushNow = _pending.Count >= _options.InboxBatchSize;

      if (!shouldFlushNow) {
        // Reset sliding window timer
        _slideTimer?.Change(_options.InboxBatchSlideMs, Timeout.Infinite);
      }
    }

    if (shouldFlushNow) {
      // Fire-and-forget flush on the thread pool
      _ = Task.Run(() => _flushBatchAsync(), CancellationToken.None);
    }

    return tcs.Task;
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    // Stop timers
    if (_slideTimer is not null) {
      await _slideTimer.DisposeAsync();
      _slideTimer = null;
    }
    if (_hardMaxTimer is not null) {
      await _hardMaxTimer.DisposeAsync();
      _hardMaxTimer = null;
    }

    // Flush any remaining pending messages
    await _flushBatchAsync();
  }

  private void _slideTimerCallback(object? state) {
    if (_disposed) {
      return;
    }
    _ = Task.Run(() => _flushBatchAsync(), CancellationToken.None);
  }

  private void _hardMaxTimerCallback(object? state) {
    if (_disposed) {
      return;
    }
    _ = Task.Run(() => _flushBatchAsync(), CancellationToken.None);
  }

  private async Task _flushBatchAsync() {
    List<PendingInbox> batch;

    lock (_lock) {
      if (_pending.Count == 0) {
        return;
      }

      batch = _pending;
      _pending = [];

      // Stop both timers
      _slideTimer?.Change(Timeout.Infinite, Timeout.Infinite);
      _hardMaxTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    try {
      // Create ONE scope, queue ALL messages, flush ONCE
      await using var scope = _scopeFactory.CreateAsyncScope();
      var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

      foreach (var item in batch) {
        strategy.QueueInboxMessage(item.Message);
      }

      var workBatch = await strategy.FlushAsync(WorkBatchOptions.None, FlushMode.Required, CancellationToken.None);

      // Record batch metrics
      _metrics?.InboxBatchSize.Record(batch.Count);
      _metrics?.InboxBatchFlushes.Add(1);
      if (batch.Count > 0) {
        var firstEnqueueTimestamp = batch[0].EnqueueTimestamp;
        var waitMs = Stopwatch.GetElapsedTime(firstEnqueueTimestamp).TotalMilliseconds;
        _metrics?.InboxBatchWaitDuration.Record(waitMs);
      }

      // Complete all waiting handlers with the same WorkBatch
      foreach (var item in batch) {
        item.Registration.Dispose();
        item.Tcs.TrySetResult(workBatch);
      }
    } catch (Exception ex) {
      // Propagate error to all waiting handlers
      foreach (var item in batch) {
        item.Registration.Dispose();
        item.Tcs.TrySetException(ex);
      }
    }
  }

  /// <summary>
  /// Represents a pending inbox message with its completion source.
  /// </summary>
  private sealed record PendingInbox(
    InboxMessage Message,
    TaskCompletionSource<WorkBatch> Tcs,
    CancellationTokenRegistration Registration,
    long EnqueueTimestamp
  );
}
