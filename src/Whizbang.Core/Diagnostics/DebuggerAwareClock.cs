// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Core.Diagnostics;

/// <summary>
/// Implementation of <see cref="IDebuggerAwareClock"/> that tracks active vs frozen time.
/// </summary>
/// <remarks>
/// <para>
/// This is a central, reusable component for debugger-aware timing across the system.
/// It can be used by perspective sync, transport layer, health checks, and other components
/// that need timeouts that don't trigger during debugging.
/// </para>
/// <para>
/// Each pause-state subscription owns a bounded buffer of the ten most recent changes and its own
/// read loop. A change is offered to every live subscription's buffer, so every subscriber
/// receives every change made after it subscribed, in order, and a slow, blocked or throwing
/// handler holds up only its own subscription. A subscriber that falls more than ten changes
/// behind loses the oldest, so the last change it receives is always the clock's current state.
/// </para>
/// </remarks>
/// <docs>extending/features/debugger-aware-clock</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Diagnostics/DebuggerAwareClockTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Diagnostics/DebuggerAwareClockCoverageTests.cs:RunPauseStateReadLoop_ReleasedWhileACallIsInFlight_DispatchesNothingAfterItAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Diagnostics/DebuggerAwareClockCoverageTests.cs:RunPauseStateReadLoop_ReleasedByItsOwnHandler_DispatchesNothingAfterItAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Diagnostics/DebuggerAwareClockPauseFanOutTests.cs</tests>
public sealed partial class DebuggerAwareClock : IDebuggerAwareClock {
  /// <summary>How many undelivered changes one subscription keeps before dropping the oldest.</summary>
  private const int PAUSE_STATE_BUFFER_CAPACITY = 10;

  private static readonly BoundedChannelOptions _pauseStateBufferOptions = new(PAUSE_STATE_BUFFER_CAPACITY) {
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true,
    SingleWriter = false
  };

  private readonly DebuggerAwareClockOptions _options;
  private readonly ILogger _logger;
  private readonly Lock _subscriptionsLock = new();
  // Copy-on-write: replaced under the lock when a subscription is added or released, read without
  // it by the sampler, so publishing a change allocates nothing and never waits on a subscriber.
  private PauseStateSubscription[] _subscriptions = [];
  private readonly Timer? _sampler;
  private readonly Process _process;
  private TimeSpan _lastCpuSample;
  private DateTimeOffset _lastSampleTime;
  private bool _isPaused;
  private bool _disposed;

  /// <summary>
  /// Initializes a new instance of <see cref="DebuggerAwareClock"/> with default options.
  /// </summary>
  public DebuggerAwareClock() : this(new DebuggerAwareClockOptions()) {
  }

  /// <summary>
  /// Initializes a new instance of <see cref="DebuggerAwareClock"/> with the specified options.
  /// </summary>
  /// <param name="options">Configuration options for the clock.</param>
  public DebuggerAwareClock(DebuggerAwareClockOptions options) : this(options, null) {
  }

  /// <summary>
  /// Initializes a new instance of <see cref="DebuggerAwareClock"/> with the specified options and
  /// a logger for the failures of pause-state handlers.
  /// </summary>
  /// <param name="options">Configuration options for the clock.</param>
  /// <param name="logger">
  /// Receives an error for each pause-state handler call that throws; <c>null</c> discards them.
  /// </param>
  public DebuggerAwareClock(DebuggerAwareClockOptions options, ILogger<DebuggerAwareClock>? logger) {
    ArgumentNullException.ThrowIfNull(options);
    _options = options;
    _logger = (ILogger?)logger ?? NullLogger.Instance;
    _process = Process.GetCurrentProcess();
    _lastCpuSample = _readCpuTime();
    _lastSampleTime = _utcNow();

    // Start sampling timer if using CPU time sampling mode
    if (_shouldUseCpuSampling()) {
      var interval = (int)_options.SamplingInterval.TotalMilliseconds;
      _sampler = new Timer(SampleCpuTime, null, interval, interval);
    }
  }

  /// <summary>Reads accumulated CPU time from the configured source, or the process.</summary>
  private TimeSpan _readCpuTime() => _options.CpuTimeSource?.Invoke() ?? _process.TotalProcessorTime;

  private bool _isDebuggerAttached() => _options.DebuggerAttachedSource?.Invoke() ?? Debugger.IsAttached;

  private DateTimeOffset _utcNow() => _options.WallClockSource?.Invoke() ?? DateTimeOffset.UtcNow;

  /// <inheritdoc />
  public DebuggerDetectionMode Mode => _options.Mode;

  /// <inheritdoc />
  public bool IsPaused {
    get {
      if (_options.Mode == DebuggerDetectionMode.Disabled) {
        return false;
      }

      return _isPaused;
    }
  }

  /// <inheritdoc />
  public IActiveStopwatch StartNew() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    return new ActiveStopwatch(this);
  }

  /// <inheritdoc />
  public IDisposable OnPauseStateChanged(Action<bool> handler) {
    ArgumentNullException.ThrowIfNull(handler);

    lock (_subscriptionsLock) {
      // Checked under the lock Dispose takes, so a subscription is either added before Dispose
      // completes every buffer or refused: none is left waiting on a buffer nothing completes.
      ObjectDisposedException.ThrowIf(_disposed, this);
      var subscription = new PauseStateSubscription(this, handler);
      _subscriptions = [.. _subscriptions, subscription];
      return subscription;
    }
  }

  /// <inheritdoc />
  public long GetCurrentTimestamp() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    return Stopwatch.GetTimestamp();
  }

  /// <inheritdoc />
  public void Dispose() {
    PauseStateSubscription[] subscriptions;
    lock (_subscriptionsLock) {
      if (_disposed) {
        return;
      }

      _disposed = true;
      subscriptions = _subscriptions;
      _subscriptions = [];
    }

    _sampler?.Dispose();
    foreach (var subscription in subscriptions) {
      subscription.Complete();
    }
  }

  /// <summary>
  /// Runs the pause-state read loop against a caller-supplied reader. Internal rather than
  /// private so the loop's faulted-channel exit can be asserted: this class only ever completes
  /// its own channels gracefully (<c>Dispose</c> calls the parameterless <c>TryComplete</c>), so a
  /// reader that throws <see cref="ChannelClosedException"/> is not reachable through
  /// <see cref="OnPauseStateChanged"/> — and the loop must still end quietly rather than leave an
  /// unobserved faulted task behind if a future caller ever completes it with an error.
  /// </summary>
  internal static Task RunPauseStateReadLoopAsync(
      ChannelReader<bool> reader, Action<bool> handler, CancellationToken ct)
    => PauseStateSubscription.ReadLoopAsync(reader, handler, NullLogger.Instance, ct);

  /// <summary>Removes a released subscription so later changes are no longer offered to it.</summary>
  private void _release(PauseStateSubscription subscription) {
    lock (_subscriptionsLock) {
      // Already gone when the clock was disposed first; the filter is then a no-op.
      _subscriptions = Array.FindAll(_subscriptions, s => !ReferenceEquals(s, subscription));
    }
  }

  /// <summary>Offers a pause-state change to every live subscription's own buffer.</summary>
  private void _publish(bool isPaused) {
    foreach (var subscription in Volatile.Read(ref _subscriptions)) {
      subscription.Offer(isPaused);
    }
  }

  private bool _shouldUseCpuSampling() {
    return _options.Mode switch {
      DebuggerDetectionMode.CpuTimeSampling => true,
      DebuggerDetectionMode.Auto => true, // Auto mode uses CPU sampling as primary detection
      _ => false
    };
  }

  /// <summary>
  /// One sampling tick. Internal rather than private so the disposed guard and each detection
  /// mode can be driven deterministically; the timer supplies the real cadence.
  /// </summary>
  internal void SampleCpuTime(object? state) {
    if (_disposed) {
      return;
    }

    var now = _utcNow();
    TimeSpan currentCpu;

    try {
      currentCpu = _readCpuTime();
    } catch (InvalidOperationException) {
      // Process may have exited
      return;
    }

    var wallDelta = now - _lastSampleTime;
    var cpuDelta = currentCpu - _lastCpuSample;

    // Determine if we're frozen based on mode
    var wasPaused = _isPaused;
    _isPaused = _options.Mode switch {
      DebuggerDetectionMode.DebuggerAttached =>
          _isDebuggerAttached() && _isFrozenBasedOnCpuTime(wallDelta, cpuDelta),
      DebuggerDetectionMode.CpuTimeSampling =>
          _isFrozenBasedOnCpuTime(wallDelta, cpuDelta),
      DebuggerDetectionMode.Auto =>
          _isDebuggerAttached() && _isFrozenBasedOnCpuTime(wallDelta, cpuDelta),
      _ => false
    };

    // Notify subscribers if pause state changed
    if (wasPaused != _isPaused) {
      _publish(_isPaused);
    }

    _lastCpuSample = currentCpu;
    _lastSampleTime = now;
  }

  private bool _isFrozenBasedOnCpuTime(TimeSpan wallDelta, TimeSpan cpuDelta) {
    // If wall time is significantly more than CPU time, we're frozen
    // Use threshold from options (default 10x)
    if (wallDelta.TotalMilliseconds < 200) {
      // Too short to determine reliably
      return false;
    }

    if (cpuDelta.TotalMilliseconds < 10) {
      // Very little CPU time used
      var ratio = wallDelta.TotalMilliseconds / Math.Max(1, cpuDelta.TotalMilliseconds);
      return ratio >= _options.FrozenThreshold;
    }

    return false;
  }

  /// <summary>
  /// Internal stopwatch implementation that tracks active time.
  /// </summary>
  private sealed class ActiveStopwatch : IActiveStopwatch {
    private readonly DebuggerAwareClock _clock;
    private readonly Stopwatch _wallStopwatch;
    private readonly TimeSpan _startCpuTime;
    private TimeSpan? _stoppedActiveElapsed;
    private TimeSpan? _stoppedWallElapsed;
    private bool _stopped;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActiveStopwatch"/> class.
    /// </summary>
    /// <param name="clock">The parent clock used for pause detection.</param>
    public ActiveStopwatch(DebuggerAwareClock clock) {
      _clock = clock;
      _wallStopwatch = Stopwatch.StartNew();

      try {
        _startCpuTime = clock._readCpuTime();
      } catch (InvalidOperationException) {
        _startCpuTime = TimeSpan.Zero;
      }
    }

    /// <inheritdoc/>
    public TimeSpan ActiveElapsed {
      get {
        if (_stopped && _stoppedActiveElapsed.HasValue) {
          return _stoppedActiveElapsed.Value;
        }

        return _calculateActiveElapsed();
      }
    }

    /// <inheritdoc/>
    public TimeSpan WallElapsed {
      get {
        if (_stopped && _stoppedWallElapsed.HasValue) {
          return _stoppedWallElapsed.Value;
        }

        return _wallStopwatch.Elapsed;
      }
    }

    /// <inheritdoc/>
    public TimeSpan FrozenTime {
      get {
        var wall = WallElapsed;
        var active = ActiveElapsed;

        if (_clock._options.Mode == DebuggerDetectionMode.Disabled) {
          return TimeSpan.Zero;
        }

        var frozen = wall - active;
        return frozen > TimeSpan.Zero ? frozen : TimeSpan.Zero;
      }
    }

    /// <inheritdoc/>
    public bool HasTimedOut(TimeSpan timeout) {
      return ActiveElapsed >= timeout;
    }

    /// <inheritdoc/>
    public void Halt() {
      if (_stopped) {
        return;
      }

      _stopped = true;
      _stoppedActiveElapsed = _calculateActiveElapsed();
      _stoppedWallElapsed = _wallStopwatch.Elapsed;
      _wallStopwatch.Stop();
    }

    private TimeSpan _calculateActiveElapsed() {
      if (_clock._options.Mode == DebuggerDetectionMode.Disabled) {
        // Disabled mode: active time equals wall time
        return _wallStopwatch.Elapsed;
      }

      if (!_clock._isDebuggerAttached() && _clock._options.Mode != DebuggerDetectionMode.CpuTimeSampling) {
        // No debugger attached and not using CPU sampling: use wall time
        return _wallStopwatch.Elapsed;
      }

      // Use CPU time sampling to calculate active time
      TimeSpan currentCpuTime;
      try {
        currentCpuTime = _clock._readCpuTime();
      } catch (InvalidOperationException) {
        // Process info not available, fall back to wall time
        return _wallStopwatch.Elapsed;
      }

      var cpuElapsed = currentCpuTime - _startCpuTime;
      var wallElapsed = _wallStopwatch.Elapsed;

      // CPU time is a lower bound on active time
      // However, CPU time only counts this process, and may be less than wall time
      // even when not paused due to I/O wait, sleep, etc.
      // We use a heuristic: if CPU time is significantly less than wall time,
      // and we detected a pause, use CPU time. Otherwise use wall time.
      if (_clock.IsPaused || (cpuElapsed < wallElapsed / 2)) {
        // Significant difference suggests frozen time
        // Use CPU elapsed, but cap it to wall elapsed
        return cpuElapsed < wallElapsed ? cpuElapsed : wallElapsed;
      }

      return wallElapsed;
    }
  }

  /// <summary>
  /// Subscription to pause state changes. Owns a bounded buffer of the latest changes and a read
  /// loop that hands them to its handler, so a handler that is slow, blocked or throwing holds up
  /// only its own subscription.
  /// </summary>
  private sealed class PauseStateSubscription : IDisposable {
    private readonly DebuggerAwareClock _clock;
    private readonly Channel<bool> _buffer;
    private readonly CancellationTokenSource _cts;
#pragma warning disable S4487 // Field keeps background task rooted to prevent GC collection
    private readonly Task _readTask;
#pragma warning restore S4487
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PauseStateSubscription"/> class.
    /// </summary>
    /// <param name="clock">The clock that offers this subscription its changes.</param>
    /// <param name="handler">The callback invoked when pause state changes.</param>
    public PauseStateSubscription(DebuggerAwareClock clock, Action<bool> handler) {
      _clock = clock;
      _buffer = Channel.CreateBounded<bool>(_pauseStateBufferOptions);
      _cts = new CancellationTokenSource();
      _readTask = ReadLoopAsync(_buffer.Reader, handler, clock._logger, _cts.Token);
    }

    /// <summary>Buffers a change for this subscription; the oldest is dropped when it is full.</summary>
    public void Offer(bool isPaused) => _buffer.Writer.TryWrite(isPaused);

    /// <summary>Ends the read loop once the buffered changes are delivered (the clock was disposed).</summary>
    public void Complete() => _buffer.Writer.TryComplete();

    /// <summary>
    /// Releases the subscription. Once this returns no further state is dispatched to the
    /// handler; a call already running is not waited for, so releasing from inside the handler,
    /// or from a thread the handler is waiting on, cannot deadlock.
    /// </summary>
    public void Dispose() {
      // Guarded because Dispose must be safe to call twice: Cancel() on an already-disposed
      // source throws, and a `using` around a subscription the caller also released explicitly
      // is an ordinary shape — the second release would then fault the caller's scope exit.
      if (_disposed) {
        return;
      }
      _disposed = true;
      _cts.Cancel();
      _cts.Dispose();
      _clock._release(this);
      Complete();
      // Don't wait for task - it will complete when canceled
    }

    internal static async Task ReadLoopAsync(
        ChannelReader<bool> reader, Action<bool> handler, ILogger logger, CancellationToken ct) {
      try {
        // Not ReadAllAsync: it checks the token only while waiting, so the states already buffered
        // when the subscription is released would still be dispatched after Dispose returned. The
        // token is checked before every read, which also leaves those states unconsumed.
        while (await reader.WaitToReadAsync(ct)) {
          while (!ct.IsCancellationRequested && reader.TryRead(out var isPaused)) {
            _dispatch(handler, isPaused, logger);
          }
        }
      } catch (OperationCanceledException) {
        // Expected when disposed
      } catch (ChannelClosedException) {
        // Expected when clock is disposed
      }
    }

    /// <summary>
    /// Calls the handler for one change. A handler that throws is logged and stays subscribed:
    /// its failure is its own, and ending its loop would silently stop every later change.
    /// </summary>
    private static void _dispatch(Action<bool> handler, bool isPaused, ILogger logger) {
      try {
        handler(isPaused);
      } catch (Exception ex) {
        LogPauseStateHandlerFailed(logger, isPaused, ex);
      }
    }
  }

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Error,
    Message = "A pause-state handler threw when told paused: {IsPaused}. The subscription stays live and receives later changes")]
  private static partial void LogPauseStateHandlerFailed(ILogger logger, bool isPaused, Exception exception);
}
