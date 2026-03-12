using System.Diagnostics;
using System.Threading.Channels;

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
/// </remarks>
/// <docs>features/debugger-aware-clock</docs>
/// <tests>Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs</tests>
public sealed class DebuggerAwareClock : IDebuggerAwareClock {
  private readonly DebuggerAwareClockOptions _options;
  private readonly Channel<bool> _pauseStateChannel;
  private readonly Timer? _sampler;
  private readonly Process _process;
  private TimeSpan _lastCpuSample;
  private DateTime _lastSampleTime;
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
  public DebuggerAwareClock(DebuggerAwareClockOptions options) {
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _pauseStateChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(10) {
      FullMode = BoundedChannelFullMode.DropOldest
    });
    _process = Process.GetCurrentProcess();
    _lastCpuSample = _process.TotalProcessorTime;
    _lastSampleTime = DateTime.UtcNow;

    // Start sampling timer if using CPU time sampling mode
    if (_shouldUseCpuSampling()) {
      var interval = (int)_options.SamplingInterval.TotalMilliseconds;
      _sampler = new Timer(_sampleCpuTime, null, interval, interval);
    }
  }

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
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(handler);

    return new PauseStateSubscription(_pauseStateChannel.Reader, handler);
  }

  /// <inheritdoc />
  public long GetCurrentTimestamp() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    return Stopwatch.GetTimestamp();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;
    _sampler?.Dispose();
    _pauseStateChannel.Writer.TryComplete();
  }

  private bool _shouldUseCpuSampling() {
    return _options.Mode switch {
      DebuggerDetectionMode.CpuTimeSampling => true,
      DebuggerDetectionMode.Auto => true, // Auto mode uses CPU sampling as primary detection
      _ => false
    };
  }

  private void _sampleCpuTime(object? state) {
    if (_disposed) {
      return;
    }

    var now = DateTime.UtcNow;
    TimeSpan currentCpu;

    try {
      currentCpu = _process.TotalProcessorTime;
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
          Debugger.IsAttached && _isFrozenBasedOnCpuTime(wallDelta, cpuDelta),
      DebuggerDetectionMode.CpuTimeSampling =>
          _isFrozenBasedOnCpuTime(wallDelta, cpuDelta),
      DebuggerDetectionMode.Auto =>
          Debugger.IsAttached && _isFrozenBasedOnCpuTime(wallDelta, cpuDelta),
      _ => false
    };

    // Notify subscribers if pause state changed
    if (wasPaused != _isPaused) {
      _pauseStateChannel.Writer.TryWrite(_isPaused);
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
    private TimeSpan _startCpuTime;
    private TimeSpan? _stoppedActiveElapsed;
    private TimeSpan? _stoppedWallElapsed;
    private bool _stopped;

    public ActiveStopwatch(DebuggerAwareClock clock) {
      _clock = clock;
      _wallStopwatch = Stopwatch.StartNew();

      try {
        _startCpuTime = clock._process.TotalProcessorTime;
      } catch (InvalidOperationException) {
        _startCpuTime = TimeSpan.Zero;
      }
    }

    public TimeSpan ActiveElapsed {
      get {
        if (_stopped && _stoppedActiveElapsed.HasValue) {
          return _stoppedActiveElapsed.Value;
        }

        return _calculateActiveElapsed();
      }
    }

    public TimeSpan WallElapsed {
      get {
        if (_stopped && _stoppedWallElapsed.HasValue) {
          return _stoppedWallElapsed.Value;
        }

        return _wallStopwatch.Elapsed;
      }
    }

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

    public bool HasTimedOut(TimeSpan timeout) {
      return ActiveElapsed >= timeout;
    }

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

      if (!Debugger.IsAttached && _clock._options.Mode != DebuggerDetectionMode.CpuTimeSampling) {
        // No debugger attached and not using CPU sampling: use wall time
        return _wallStopwatch.Elapsed;
      }

      // Use CPU time sampling to calculate active time
      TimeSpan currentCpuTime;
      try {
        currentCpuTime = _clock._process.TotalProcessorTime;
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
  /// Subscription to pause state changes.
  /// </summary>
  private sealed class PauseStateSubscription : IDisposable {
    private readonly CancellationTokenSource _cts;
#pragma warning disable S4487 // Field keeps background task rooted to prevent GC collection
    private readonly Task _readTask;
#pragma warning restore S4487

    public PauseStateSubscription(ChannelReader<bool> reader, Action<bool> handler) {
      _cts = new CancellationTokenSource();
      _readTask = _readLoopAsync(reader, handler, _cts.Token);
    }

    public void Dispose() {
      _cts.Cancel();
      _cts.Dispose();
      // Don't wait for task - it will complete when cancelled
    }

    private static async Task _readLoopAsync(ChannelReader<bool> reader, Action<bool> handler, CancellationToken ct) {
      try {
        await foreach (var isPaused in reader.ReadAllAsync(ct)) {
          handler(isPaused);
        }
      } catch (OperationCanceledException) {
        // Expected when disposed
      } catch (ChannelClosedException) {
        // Expected when clock is disposed
      }
    }
  }
}
