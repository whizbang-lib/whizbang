namespace Whizbang.Core.Workers;

/// <summary>
/// Transport-agnostic batch collector that buffers messages and flushes them via a callback
/// when a batch is ready. Used by transports to collect received messages before bulk-inserting
/// into the Postgres inbox.
/// </summary>
/// <remarks>
/// <para>
/// Three flush triggers (whichever fires first):
/// <list type="bullet">
///   <item><b>Batch size</b>: <see cref="TransportBatchOptions.BatchSize"/> messages → immediate flush</item>
///   <item><b>Sliding window</b>: <see cref="TransportBatchOptions.SlideMs"/> ms of quiet → flush partial batch</item>
///   <item><b>Hard max</b>: <see cref="TransportBatchOptions.MaxWaitMs"/> ms since first message → flush regardless</item>
/// </list>
/// </para>
/// <para>
/// Thread-safe: multiple transport event handlers can call <see cref="Enqueue"/> concurrently.
/// The flush callback is invoked on the thread pool.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of transport message to collect.</typeparam>
/// <docs>messaging/transports/transport-consumer#batch-collector</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportBatchCollectorTests.cs</tests>
public sealed class TransportBatchCollector<T> : IAsyncDisposable {
  private readonly TransportBatchOptions _options;
  private readonly Func<IReadOnlyList<T>, Task> _flushCallback;

  private readonly Lock _lock = new();
  private List<T> _pending = [];
  private Timer? _slideTimer;
  private Timer? _hardMaxTimer;
  private bool _disposed;

  /// <summary>
  /// Event fired after a batch has been successfully flushed via the callback.
  /// Useful for synchronization in tests and production monitoring.
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#batch-collector</docs>
  public event Action<int>? OnBatchFlushed;

  /// <summary>
  /// Initializes a new <see cref="TransportBatchCollector{T}"/>.
  /// </summary>
  /// <param name="options">Batch size, sliding window, and hard max configuration.</param>
  /// <param name="flushCallback">Callback invoked with the collected batch when a flush triggers.</param>
  public TransportBatchCollector(TransportBatchOptions options, Func<IReadOnlyList<T>, Task> flushCallback) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(flushCallback);

    _options = options;
    _flushCallback = flushCallback;
    _slideTimer = new Timer(_slideTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    _hardMaxTimer = new Timer(_hardMaxTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
  }

  /// <summary>
  /// Enqueues a message for batching. Returns immediately — does not block.
  /// The flush callback will be invoked when a batch trigger fires.
  /// </summary>
  /// <param name="message">The transport message to collect.</param>
  public void Enqueue(T message) {
    if (_disposed) {
      return;
    }

    bool shouldFlushNow;

    lock (_lock) {
      _pending.Add(message);

      // Start hard max timer on first message in batch
      if (_pending.Count == 1) {
        _hardMaxTimer?.Change(_options.MaxWaitMs, Timeout.Infinite);
      }

      // Check batch size trigger
      shouldFlushNow = _pending.Count >= _options.BatchSize;

      if (!shouldFlushNow) {
        // Reset sliding window timer
        _slideTimer?.Change(_options.SlideMs, Timeout.Infinite);
      }
    }

    if (shouldFlushNow) {
      _ = Task.Run(() => _flushBatchAsync());
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    _disposed = true;

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

    _ = Task.Run(() => _flushBatchAsync());
  }

  private void _hardMaxTimerCallback(object? state) {
    if (_disposed) {
      return;
    }

    _ = Task.Run(() => _flushBatchAsync());
  }

  private async Task _flushBatchAsync() {
    List<T> batch;

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
      await _flushCallback(batch);
      OnBatchFlushed?.Invoke(batch.Count);
    } catch (Exception) {
      // Re-add failed batch to pending for retry on next flush
      lock (_lock) {
        batch.AddRange(_pending);
        _pending = batch;

        // Restart slide timer so the batch gets retried
        _slideTimer?.Change(_options.SlideMs, Timeout.Infinite);
      }

      throw;
    }
  }
}
