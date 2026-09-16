namespace Whizbang.Core.Signals;

/// <summary>
/// Abstract base for concrete pull signal sources: schedules a periodic tick against an injected
/// <see cref="TimeProvider"/>, calls <see cref="DetectAsync"/> each tick, and raises a default
/// (<em>doorbell</em>) <typeparamref name="TSignal"/> on the sink whenever detection returns true.
/// Deterministic tests use <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c> and drive the
/// tick via <see cref="TickForTestsAsync"/> — no <c>Task.Delay</c>, no timing races.
/// </summary>
/// <remarks>
/// With an <paramref name="idleBackoff"/>, a source whose ticks keep finding nothing stretches its
/// interval toward a ceiling and returns to the base interval on the first hit or on any
/// <see cref="Reschedule"/>. Without one the interval is fixed, as it always was.
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Signals/PollSignalSourceIdleBackoffTests.cs</tests>
public abstract class BasePollSignalSource<TSignal>(
  TimeProvider clock,
  TimeSpan interval,
  PollIdleBackoff? idleBackoff = null
) : IPollSignalSource<TSignal> where TSignal : ISignal, new() {
  private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
  private readonly PollIdleBackoff? _idleBackoff = idleBackoff;
  private TimeSpan _baseInterval = interval > TimeSpan.Zero
    ? interval
    : throw new ArgumentOutOfRangeException(nameof(interval), "Poll interval must be positive.");
  private TimeSpan _interval = interval;
  private int _emptyStreak;
  private ISignalSink? _sink;
  private ITimer? _timer;
  private readonly Lock _timerGate = new();

  /// <inheritdoc />
  public TimeSpan Interval {
    get { lock (_timerGate) { return _interval; } }
  }

  /// <inheritdoc />
  public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(sink);
    _sink = sink;
    lock (_timerGate) {
      _timer = _clock.CreateTimer(_onTick, state: null, _interval, _interval);
    }
    return Task.CompletedTask;
  }

  /// <summary>
  /// Change the polling interval at runtime. Used by concrete sources that adapt their cadence
  /// to external state (e.g., tightening when a push transport reports unavailable). A no-op
  /// before <see cref="StartAsync"/> — reschedules the running timer otherwise. The new interval
  /// is the base the idle backoff stretches from, and the empty streak restarts.
  /// </summary>
  protected void Reschedule(TimeSpan newInterval) {
    if (newInterval <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(newInterval), "Poll interval must be positive.");
    }
    lock (_timerGate) {
      _baseInterval = newInterval;
      _emptyStreak = 0;
      _applyInterval(newInterval);
    }
  }

  /// <summary>
  /// Run one detection cycle synchronously (test hook). Production callers rely on the timer
  /// scheduled by <see cref="StartAsync"/>; this method exists so unit tests can advance a fake
  /// clock and observe the exact effect without racing a background timer.
  /// </summary>
  public ValueTask TickForTestsAsync(CancellationToken cancellationToken) {
    if (_sink is null) {
      throw new InvalidOperationException("Poll source not started — call StartAsync before ticking.");
    }
    return _tickAsync(_sink, cancellationToken);
  }

  /// <summary>
  /// Detect whether the target condition currently holds. Returning true raises the doorbell
  /// signal; false is a no-op. Implementations must be non-blocking and fast (poll sources run
  /// on the bus's tick schedule).
  /// </summary>
  protected abstract ValueTask<bool> DetectAsync(CancellationToken cancellationToken);

  private void _onTick(object? state) {
    var sink = _sink;
    if (sink is null) {
      return;
    }
    // Fire-and-forget — the timer callback does not await; we observe exceptions to keep the loop.
    _ = _tickObserveAsync(sink);
  }

  private async Task _tickObserveAsync(ISignalSink sink) {
    try {
      await _tickAsync(sink, CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) {
      // Poll-source ticks must never crash the timer thread. Delegate to the subclass so
      // concrete sources can log via their injected ILogger; the default is a no-op so a
      // custom source that forgets to override still gets crash-free semantics.
      OnTickError(ex);
    }
  }

  /// <summary>
  /// Called when a tick raises an exception (from <see cref="DetectAsync"/> or from the sink's
  /// receive). Default is a no-op — concrete sources should override to log via their injected
  /// <see cref="Microsoft.Extensions.Logging.ILogger"/>. The exception is swallowed after this
  /// callback so the timer thread stays alive.
  /// </summary>
  protected virtual void OnTickError(Exception ex) { }

  private async ValueTask _tickAsync(ISignalSink sink, CancellationToken cancellationToken) {
    var detected = await DetectAsync(cancellationToken).ConfigureAwait(false);
    _recordOutcome(detected);
    if (detected) {
      await sink.ReceiveAsync(new TSignal(), cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>Stretches or restores the interval from what the tick found; nothing without a backoff.</summary>
  private void _recordOutcome(bool detected) {
    if (_idleBackoff is null) {
      return;
    }
    lock (_timerGate) {
      _emptyStreak = detected ? 0 : _emptyStreak + 1;
      _applyInterval(_idleBackoff.IntervalAfter(_baseInterval, _emptyStreak));
    }
  }

  /// <summary>Sets the effective interval, moving the timer only when it changes. Caller holds the gate.</summary>
  private void _applyInterval(TimeSpan effective) {
    if (effective == _interval) {
      return;
    }
    _interval = effective;
    _timer?.Change(effective, effective);
  }
}
