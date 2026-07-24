using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Whizbang.Core.Workers;

/// <summary>
/// Reads items from a <see cref="ChannelReader{T}"/> and yields them in batches
/// using a sliding-window + max-wait + max-size policy. The policy mirrors
/// debounced-batch coalescing common in messaging pipelines: collect signals
/// into a batch, give each new signal a short quiet period to accumulate more,
/// but never wait longer than a hard cap.
/// </summary>
/// <remarks>
/// <para>
/// Three flush triggers, whichever fires first wins:
/// </para>
/// <list type="number">
///   <item><b>MaxSize</b> reached: the batch is full.</item>
///   <item><b>SlidingWindow</b> elapsed with no new arrivals (debounce): the producer has gone quiet.</item>
///   <item><b>MaxWait</b> elapsed from the first arrival (hard cap): even a busy producer must flush eventually.</item>
/// </list>
/// <para>
/// All three are exposed via <see cref="SlidingWindowBatcherOptions"/> with sensible defaults.
/// Time is measured via injected <see cref="TimeProvider"/> so tests can advance the clock
/// without real <c>Task.Delay</c> calls.
/// </para>
/// </remarks>
public sealed class SlidingWindowBatcher<T> {
  private readonly ChannelReader<T> _reader;
  private readonly SlidingWindowBatcherOptions _options;
  private readonly TimeProvider _timeProvider;

  /// <summary>
  /// Creates a batcher over the given reader. <paramref name="timeProvider"/> defaults to
  /// <see cref="TimeProvider.System"/>.
  /// </summary>
  public SlidingWindowBatcher(
      ChannelReader<T> reader,
      SlidingWindowBatcherOptions options,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(reader);
    ArgumentNullException.ThrowIfNull(options);
    if (options.MaxSize < 1) {
      throw new ArgumentOutOfRangeException(nameof(options), "MaxSize must be >= 1.");
    }
    if (options.SlidingWindow < TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(options), "SlidingWindow must be >= 0.");
    }
    if (options.MaxWait <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(options), "MaxWait must be > 0.");
    }
    if (options.SlidingWindow > options.MaxWait) {
      throw new ArgumentOutOfRangeException(nameof(options), "SlidingWindow must be <= MaxWait.");
    }
    _reader = reader;
    _options = options;
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

  /// <summary>
  /// Yields batches of items as they accumulate. Completes when the underlying channel
  /// is closed and drained. Throws <see cref="OperationCanceledException"/> on cancellation.
  /// </summary>
  public async IAsyncEnumerable<IReadOnlyList<T>> ReadBatchesAsync(
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    while (!cancellationToken.IsCancellationRequested) {
      // Block until at least one item is available (or the channel closes).
      bool hasItem;
      try {
        hasItem = await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        yield break;
      }
      if (!hasItem) {
        yield break;
      }

      var batch = new List<T>(_options.MaxSize);
      // Drain whatever is currently buffered without waiting.
      while (batch.Count < _options.MaxSize && _reader.TryRead(out var first)) {
        batch.Add(first);
      }
      if (batch.Count == 0) {
        // WaitToReadAsync said true but TryRead returned nothing — race with another consumer.
        // Loop to re-wait.
        continue;
      }

      var firstArrival = _timeProvider.GetTimestamp();
      var lastArrival = firstArrival;

      // Accumulate further arrivals until a flush trigger fires.
      while (batch.Count < _options.MaxSize) {
        var elapsedSinceLast = _timeProvider.GetElapsedTime(lastArrival);
        var elapsedSinceFirst = _timeProvider.GetElapsedTime(firstArrival);
        var slidingRemaining = _options.SlidingWindow - elapsedSinceLast;
        var maxWaitRemaining = _options.MaxWait - elapsedSinceFirst;
        var waitFor = slidingRemaining < maxWaitRemaining ? slidingRemaining : maxWaitRemaining;

        if (waitFor <= TimeSpan.Zero) {
          break; // sliding window or max wait elapsed
        }

        // Wait for either a new arrival or the wait window to expire.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = _reader.WaitToReadAsync(waitCts.Token).AsTask();
        var timerTask = Task.Delay(waitFor, _timeProvider, waitCts.Token);
        var completed = await Task.WhenAny(waitTask, timerTask).ConfigureAwait(false);
        await waitCts.CancelAsync();

        if (cancellationToken.IsCancellationRequested) {
          yield break;
        }

        if (completed == timerTask) {
          // Sliding-window or max-wait expired without new arrivals — flush.
          break;
        }

        // New arrival is available. Drain whatever is buffered now.
        bool waitResult;
        try {
          waitResult = await waitTask.ConfigureAwait(false);
        } catch (OperationCanceledException) {
          yield break;
        }
        if (!waitResult) {
          // Channel closed during wait — yield current batch and exit on next loop.
          break;
        }

        var arrivedThisTurn = false;
        while (batch.Count < _options.MaxSize && _reader.TryRead(out var next)) {
          batch.Add(next);
          arrivedThisTurn = true;
        }
        if (arrivedThisTurn) {
          lastArrival = _timeProvider.GetTimestamp();
        }
      }

      if (batch.Count > 0) {
        yield return batch;
      }
    }
  }
}

/// <summary>
/// Configuration for <see cref="SlidingWindowBatcher{T}"/>.
/// </summary>
public sealed record SlidingWindowBatcherOptions {
  /// <summary>
  /// Maximum items in a single batch. The batch is flushed as soon as this is reached.
  /// Default: 100.
  /// </summary>
  public int MaxSize { get; init; } = 100;

  /// <summary>
  /// Quiet period after the last arrival; resets on each new arrival. When this elapses
  /// with no new arrivals, the current batch flushes. Default: 50 ms.
  /// </summary>
  public TimeSpan SlidingWindow { get; init; } = TimeSpan.FromMilliseconds(50);

  /// <summary>
  /// Hard cap on the wait time from the first arrival in a batch. Even a busy producer
  /// will not delay flushing past this. Default: 1 second.
  /// </summary>
  public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(1);
}
