using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// In-process per-stream FIFO serializer. Items enqueued for the same stream are processed
/// strictly in order by a single dedicated worker; different streams process in parallel via
/// independent workers. Provides stream affinity at the receive boundary regardless of
/// transport-level guarantees, defending downstream invariants (cursor monotonicity, projection
/// consistency) against transport reorder + parallel-consumer race.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Use case.</strong> A transport consumer (ASB, RabbitMQ) handles inbound messages on
/// many threads. Same-stream messages can finish processing in non-monotonic order, leaving
/// downstream <c>wh_inbox</c> / <c>wh_perspective_events</c> rows in cross-stream disorder which
/// the perspective drain then rewinds repeatedly. Routing through this serializer keyed by
/// <c>stream_id</c> guarantees that all same-stream work runs through one worker — no race —
/// while preserving cross-stream parallelism.
/// </para>
/// <para>
/// <strong>Sort-on-drain.</strong> Even with one worker per stream, two enqueue threads can
/// complete their channel writes in non-deterministic order. The optional <c>sortComparer</c> +
/// <c>DrainBatchWindow</c> let the worker accumulate near-simultaneous arrivals briefly and
/// process them in canonical order (e.g., MessageId-asc).
/// </para>
/// <para>
/// <strong>Memory bound.</strong> Each stream's channel + worker is held while the stream is
/// active. After <see cref="PerStreamSerializerOptions.IdleEvictionWindow"/> with no new items,
/// the channel is disposed; the next item for that stream creates a fresh one.
/// </para>
/// </remarks>
/// <typeparam name="T">The item type — typically a transport message envelope or work item.</typeparam>
/// <docs>internals/stream-affinity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerStreamSerializerTests.cs</tests>
public sealed class PerStreamSerializer<T> : IAsyncDisposable {
  private readonly Func<T, Guid?> _streamIdSelector;
  private readonly Func<T, CancellationToken, Task> _processor;
  private readonly PerStreamSerializerOptions _options;
  private readonly IComparer<T>? _sortComparer;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;

  private readonly ConcurrentDictionary<Guid, StreamChannel> _streams = new();
  private readonly CancellationTokenSource _stopCts = new();
  private readonly ITimer _idleSweepTimer;
  private int _disposed;

  /// <summary>
  /// Creates a per-stream serializer with the given stream-id selector and item processor.
  /// </summary>
  /// <param name="streamIdSelector">Extracts the stream-affinity key from each item; null returns route to a shared default channel.</param>
  /// <param name="processor">Per-item handler; called serially within a stream, in parallel across streams.</param>
  /// <param name="logger">Logger; processor exceptions get logged at Error.</param>
  /// <param name="sortComparer">Optional sort applied to each batch within a drain window — resolves brief enqueue races between concurrent producers.</param>
  /// <param name="options">Tuning knobs (channel capacity, drain window, idle eviction). Defaults if null.</param>
  /// <param name="timeProvider">Time source for idle eviction + drain-window timing. Pass <see cref="TimeProvider.System"/> in production, fake in tests.</param>
  public PerStreamSerializer(
      Func<T, Guid?> streamIdSelector,
      Func<T, CancellationToken, Task> processor,
      ILogger logger,
      IComparer<T>? sortComparer = null,
      PerStreamSerializerOptions? options = null,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(streamIdSelector);
    ArgumentNullException.ThrowIfNull(processor);
    _streamIdSelector = streamIdSelector;
    _processor = processor;
    _options = options ?? new PerStreamSerializerOptions();
    _sortComparer = sortComparer;
    _timeProvider = timeProvider ?? TimeProvider.System;
    _logger = logger;

    _idleSweepTimer = _timeProvider.CreateTimer(
      static state => ((PerStreamSerializer<T>)state!)._fireAndForgetIdleSweep(),
      state: this,
      dueTime: _options.IdleSweepInterval,
      period: _options.IdleSweepInterval);
  }

  /// <summary>Active stream channel count — exposed for tests + diagnostics.</summary>
  public int ActiveStreamCount => _streams.Count;

  /// <summary>
  /// Enqueues an item for serial processing under its stream key. Awaits if the per-stream
  /// channel is full (backpressure). Items with a null stream id route to a default channel.
  /// </summary>
  public async ValueTask EnqueueAsync(T item, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    var key = _streamIdSelector(item) ?? Guid.Empty;
    var stream = _streams.GetOrAdd(key, k => _createStreamChannel(k));
    stream.LastActivity = _timeProvider.GetUtcNow();
    await stream.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Completes all stream channels, awaits their workers, and stops the idle sweep timer.
  /// After this returns, no more items will process. Idempotent.
  /// </summary>
  public async Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }
    await _idleSweepTimer.DisposeAsync().ConfigureAwait(false);
    foreach (var (_, stream) in _streams) {
      stream.Writer.TryComplete();
    }
    var workers = _streams.Values.Select(s => s.Worker).ToArray();
    try {
      await Task.WhenAll(workers).WaitAsync(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      // shutdown deadline hit; remaining workers will observe _stopCts when canceled
      await _stopCts.CancelAsync().ConfigureAwait(false);
    }
    _stopCts.Dispose();
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    await FlushAndStopAsync(CancellationToken.None).ConfigureAwait(false);
  }

  /// <summary>
  /// Test helper: triggers an idle-sweep pass synchronously and waits for completion.
  /// Production code relies on the periodic timer.
  /// </summary>
  public Task RunIdleSweepNowAsync() => _runIdleSweepAsync();

  /// <summary>
  /// Test seam: the drain workers of every mapped stream channel.
  /// </summary>
  /// <remarks>
  /// How a worker's loop ENDED is invisible from outside: a shutdown absorbed by the loop's own
  /// catch and one that escapes it both stop draining, and <see cref="FlushAndStopAsync"/> folds a
  /// faulted worker into the same catch it uses for a caller-canceled shutdown, so it swallows the
  /// difference in production too. The task's final state is the only evidence, and a worker that
  /// faults instead of returning is an unobserved exception nobody ever sees.
  /// </remarks>
  internal Task WhenWorkersStoppedForTests() => Task.WhenAll(_streams.Values.Select(s => s.Worker));

  /// <summary>
  /// Test helper: spins until every stream channel is empty (or the timeout elapses).
  /// Useful when the test needs to assert post-processing state without arbitrary delays.
  /// </summary>
  public async Task WaitForIdleAsync(TimeSpan timeout) {
    var deadline = _timeProvider.GetUtcNow() + timeout;
    while (_timeProvider.GetUtcNow() < deadline) {
      var allEmpty = _streams.Values.All(s => s.Reader.Count == 0);
      if (allEmpty) {
        return;
      }
      await Task.Delay(10, _stopCts.Token).ConfigureAwait(false);
    }
  }

  private StreamChannel _createStreamChannel(Guid key) {
    var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.StreamChannelCapacity) {
      SingleReader = true,
      SingleWriter = false,
      FullMode = BoundedChannelFullMode.Wait,
    });
    var stream = new StreamChannel(key, channel, _timeProvider.GetUtcNow());
    stream.Worker = Task.Run(() => _drainStreamAsync(stream), _stopCts.Token);
    return stream;
  }

  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Draining one stream's channel implements a batch window: read the first item, then race further arrivals against the remaining window and the capacity bound, sort when a comparer is configured, and hand the items over one at a time with cancellation checked between them. The race is the method.")]
  private async Task _drainStreamAsync(StreamChannel stream) {
    var batch = new List<T>(capacity: 16);
    try {
      while (await stream.Reader.WaitToReadAsync(_stopCts.Token).ConfigureAwait(false)) {
        batch.Clear();
        // Read first item synchronously (we know one is available).
        if (!stream.Reader.TryRead(out var first)) {
          continue;
        }
        batch.Add(first);

        // Optional drain window: collect additional near-simultaneous arrivals so a sort-comparer
        // can resolve the brief enqueue race deterministically.
        if (_options.DrainBatchWindow > TimeSpan.Zero) {
          var deadline = _timeProvider.GetUtcNow() + _options.DrainBatchWindow;
          while (true) {
            // Drain whatever is currently buffered without further waiting.
            while (stream.Reader.TryRead(out var more)) {
              batch.Add(more);
            }
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero || batch.Count >= _options.StreamChannelCapacity) {
              break;
            }
            // Race "next arrival" against a wall-clock delay using the injected TimeProvider
            // so tests can advance the clock deterministically.
            using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token);
            var arrivalTask = stream.Reader.WaitToReadAsync(tickCts.Token).AsTask();
            var delayTask = Task.Delay(remaining, _timeProvider, tickCts.Token);
            var winner = await Task.WhenAny(arrivalTask, delayTask).ConfigureAwait(false);
            await tickCts.CancelAsync().ConfigureAwait(false);
            if (winner == delayTask) {
              break;
            }
            if (!await StreamBatchWindow.ContinueBatchingAsync(arrivalTask).ConfigureAwait(false)) {
              break; // channel completed, or shutdown observed while waiting for the next arrival
            }
          }
        }

        if (_sortComparer is not null && batch.Count > 1) {
          batch.Sort(_sortComparer);
        }

        foreach (var item in batch) {
          if (_stopCts.IsCancellationRequested) {
            return;
          }
          try {
            await _processor(item, _stopCts.Token).ConfigureAwait(false);
          } catch (OperationCanceledException) when (_stopCts.IsCancellationRequested) {
            return;
          } catch (Exception ex) {
            // Isolate failure to this stream — log and continue draining. Caller's processor
            // is responsible for routing to failure channels / retry. We just keep this
            // stream's worker alive so subsequent items still process serially.
            _logUnhandledProcessorException(ex, stream.Key);
          }
        }
      }
    } catch (OperationCanceledException) {
      // shutdown
    }
  }

  // The timer callback is static so the timer does not root a closure; the sweep runs fire-and-forget.

  private void _fireAndForgetIdleSweep() => _ = _runIdleSweepAsync();


  private async Task _runIdleSweepAsync() {
    if (Volatile.Read(ref _disposed) != 0) {
      return;
    }
    var cutoff = _timeProvider.GetUtcNow() - _options.IdleEvictionWindow;
    foreach (var (key, stream) in _streams) {
      if (stream.LastActivity > cutoff) {
        continue;
      }
      // Still drain any in-flight items by completing the writer; the worker will exit naturally.
      if (!_streams.TryRemove(KeyValuePair.Create(key, stream))) {
        continue;
      }
      stream.Writer.TryComplete();
      try {
        await stream.Worker.ConfigureAwait(false);
      } catch {
        // Worker errors already logged inside _drainStreamAsync; sweep continues.
      }
    }
  }

  private void _logUnhandledProcessorException(Exception ex, Guid streamKey) {
#pragma warning disable CA1848
    _logger.LogError(ex,
      "PerStreamSerializer<{ItemType}>: processor threw for stream {StreamKey}; continuing",
      typeof(T).Name, streamKey);
#pragma warning restore CA1848
  }

  private sealed class StreamChannel(Guid key, Channel<T> channel, DateTimeOffset createdAt) {
    public Guid Key { get; } = key;
    public ChannelReader<T> Reader => channel.Reader;
    public ChannelWriter<T> Writer => channel.Writer;
    public DateTimeOffset LastActivity { get; set; } = createdAt;
    public Task Worker { get; set; } = Task.CompletedTask;
  }
}

/// <summary>
/// The one decision the per-stream batch window makes about an arrival that won the race against
/// its own delay: keep batching, or stop.
/// </summary>
/// <remarks>
/// Split out and internal because both answers are otherwise unassertable. The arrival wait and
/// the window delay share one linked token, so a shutdown cancels them together and which one
/// <see cref="Task.WhenAny"/> reports first is not something a test can pin down — yet the
/// canceled answer has to close the window rather than escape, or a forced shutdown faults a
/// stream worker that nobody awaits.
/// </remarks>
internal static class StreamBatchWindow {
  /// <summary>
  /// Awaits an arrival that has already completed, answering whether the batch window should
  /// keep collecting. A closed channel and an observed cancellation both answer <c>false</c>.
  /// </summary>
  internal static async Task<bool> ContinueBatchingAsync(Task<bool> arrivalTask) {
    try {
      return await arrivalTask.ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return false;
    }
  }
}
