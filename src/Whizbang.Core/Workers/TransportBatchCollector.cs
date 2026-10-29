// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportBatchCollectorTests.cs</tests>
public sealed class TransportBatchCollector<T> : IAsyncDisposable {
  /// <summary>The flushes started by a full batch or a timer that are still running; disposal waits for them (#1721).</summary>
  internal Messaging.DetachedLifecycleWork InFlightFlushes { get; } = new();

  private readonly TransportBatchOptions _options;
  private readonly Func<IReadOnlyList<T>, Task> _flushCallback;

  private readonly Lock _lock = new();
  private List<T> _pending = [];
  // Never nulled: Change on a disposed timer returns false and does nothing, which is exactly what a
  // late enqueue or the final flush during DisposeAsync needs.
  private readonly ITimer _slideTimer;
  private readonly ITimer _hardMaxTimer;
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
  /// <param name="timeProvider">
  /// The clock the sliding window and the hard max run on. Null uses <see cref="TimeProvider.System"/>; a transport
  /// passes its own clock, and a test passes a fake one so a window closes only when the test moves time.
  /// </param>
  /// <docs>messaging/transports/transports</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportBatchCollectorTests.cs:Enqueue_FlushesOnSlidingWindowTimeoutAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportBatchCollectorTests.cs:Enqueue_HardMaxIsNotPostponedByLaterArrivalsAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportBatchCollectorTests.cs:Constructor_WithoutAClock_RunsOnTheSystemClockAsync</tests>
  public TransportBatchCollector(
      TransportBatchOptions options,
      Func<IReadOnlyList<T>, Task> flushCallback,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(flushCallback);

    _options = options;
    _flushCallback = flushCallback;
    var clock = timeProvider ?? TimeProvider.System;
    _slideTimer = clock.CreateTimer(SlideTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    _hardMaxTimer = clock.CreateTimer(_hardMaxTimerCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
        _hardMaxTimer.Change(TimeSpan.FromMilliseconds(_options.MaxWaitMs), Timeout.InfiniteTimeSpan);
      }

      // Check batch size trigger
      shouldFlushNow = _pending.Count >= _options.BatchSize;

      if (!shouldFlushNow) {
        // Reset sliding window timer
        _slideTimer.Change(TimeSpan.FromMilliseconds(_options.SlideMs), Timeout.InfiniteTimeSpan);
      }
    }

    if (shouldFlushNow) {
      _startBackgroundFlush();
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    await _slideTimer.DisposeAsync();
    await _hardMaxTimer.DisposeAsync();

    // The flushes already running finish first: each hands its batch to the callback, and the owner disposes the
    // collector so that nothing is handed over after it is gone (#1721)
    // Not from inside one of those flushes (its callback disposing its own subscription): that flush cannot finish
    // until this disposal does, so waiting for it would never end. The flush finishes once the callback returns.
    if (!TransportBatchFlushScope.IsInsideFlushOf(this)) {
      await InFlightFlushes.DrainAsync(CancellationToken.None);
    }

    // Flush any remaining pending messages
    await _flushBatchAsync();
  }

  /// <summary>
  /// One timer tick. Internal rather than private so the disposed guard can be driven
  /// deterministically; the timer supplies the real cadence.
  /// </summary>
  internal void SlideTimerTick(object? state) {
    if (_disposed) {
      return;
    }

    _startBackgroundFlush();
  }

  // Recorded, so disposal waits for it instead of returning while it still hands a batch to the callback (#1721)
  private void _startBackgroundFlush() {
#pragma warning disable RCS1075 // Exceptions handled in _flushBatchAsync (batch re-queued for retry)
    InFlightFlushes.Track(Task.Run(async () => { try { await _flushBatchAsync(); } catch { /* re-queued in _flushBatchAsync */ } }));
#pragma warning restore RCS1075
  }

  private void _hardMaxTimerCallback(object? state) {
    // Slide and hard-max timers currently share the same flush-on-fire behaviour.
    // Delegate to the slide callback so the two stay identical by construction
    // (fixes sonar S4144).
    SlideTimerTick(state);
  }

  // Marks the callback as this collector's flush while it runs, so a disposal it reaches does not wait for it
  private Task _invokeFlushCallbackAsync(List<T> batch) => TransportBatchFlushScope.Run(this, batch, _flushCallback);

  private async Task _flushBatchAsync() {
    List<T> batch;

    lock (_lock) {
      if (_pending.Count == 0) {
        return;
      }

      batch = _pending;
      _pending = [];

      // Stop both timers
      _slideTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
      _hardMaxTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    try {
      await _invokeFlushCallbackAsync(batch);
      OnBatchFlushed?.Invoke(batch.Count);
    } catch (Exception) {
      // Re-add failed batch to pending for retry on next flush
      lock (_lock) {
        batch.AddRange(_pending);
        _pending = batch;

        // Restart slide timer so the batch gets retried
        _slideTimer.Change(TimeSpan.FromMilliseconds(_options.SlideMs), Timeout.InfiniteTimeSpan);
      }

      throw;
    }
  }
}

/// <summary>
/// Which transport batch collector's flush callback is running: on this thread, for the callback's synchronous part
/// and every continuation it runs inline (an in-process request completing its reply), and along the callback's own
/// awaited path. A disposal reached from either is inside that flush and must not wait for it (#1721).
/// </summary>
/// <remarks>Not in the generic collector, so one record serves every closed collector type.</remarks>
internal static class TransportBatchFlushScope {
  [ThreadStatic] private static object? _onThisThread;
  private static readonly AsyncLocal<object?> _onThisPath = new();

  /// <summary>Runs <paramref name="callback"/> as <paramref name="collector"/>'s flush.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1229:Use async/await when necessary", Justification = "Deliberate: the thread marker covers only the callback's synchronous part, which is where continuations it completes run inline, and is restored as soon as that part returns. The awaited path is covered by the async-local marker, which flows on its own.")]
  public static Task Run<T>(object collector, List<T> batch, Func<IReadOnlyList<T>, Task> callback) {
    var outer = _onThisThread;
    _onThisThread = collector;
    _onThisPath.Value = collector;
    try {
      return callback(batch);
    } finally {
      _onThisThread = outer;
    }
  }

  /// <summary>Whether the caller is inside <paramref name="collector"/>'s flush.</summary>
  public static bool IsInsideFlushOf(object collector) =>
    ReferenceEquals(_onThisThread, collector) || ReferenceEquals(_onThisPath.Value, collector);
}
