namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Adaptive session-acceptor policy for one session-enabled subscription: concurrency starts at
/// a small floor and scales with OBSERVED active-session demand instead of standing up
/// <see cref="AzureServiceBusOptions.MaxConcurrentSessions"/> acceptors permanently. Every idle
/// acceptor slot re-polls the broker each <see cref="AzureServiceBusOptions.SessionIdleTimeout"/>
/// — a billable namespace request — so a standing army's idle cost scales with the CEILING while
/// an adaptive pool's idle cost trends to the floor by construction.
/// </summary>
/// <remarks>
/// <para>
/// Policy: GROW (double, capped at the ceiling) when active sessions have held at or above 80%
/// of current concurrency for one full evaluation window; DECAY (halve, floored) after a full
/// window with active sessions below 25% of current. Between those bands the pool holds. A
/// growth or decay step restarts both windows so the next decision is measured against the new
/// pool size.
/// </para>
/// <para>
/// The governor is deterministic and owns no threads: time flows through the injected
/// <see cref="TimeProvider"/>, session demand arrives via
/// <see cref="OnSessionInitializing"/>/<see cref="OnSessionClosing"/> (the transport's session
/// lifecycle hooks), and <see cref="Evaluate"/> is called by the transport on those same events
/// plus a cheap periodic tick. Sustain is measured across evaluation points — a dip between two
/// evaluations is invisible, which is deliberate: the evaluation cadence IS the sample rate.
/// </para>
/// </remarks>
/// <docs>messaging/transports/azure-service-bus#adaptive-acceptors</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbAcceptorGovernorTests.cs</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbAcceptorAdaptiveWiringTests.cs</tests>
public sealed class AsbAcceptorGovernor {
  // Growth fires when active sessions occupy ≥ 80% of the current pool — near-saturation means
  // pending sessions are probably queueing behind the acceptor cap.
  private const double GROWTH_PRESSURE_RATIO = 0.8;
  // Decay fires when active sessions fall below 25% of the current pool — most slots are pure
  // idle accept churn.
  private const double DECAY_IDLE_RATIO = 0.25;
  // Doubling/halving reaches any ceiling in O(log n) windows without per-step tuning knobs.
  private const int SCALE_FACTOR = 2;

  private readonly TimeProvider _timeProvider;
  private readonly TimeSpan _evaluationWindow;
  private readonly Lock _sync = new();
  private int _activeSessions;
  private int _currentConcurrency;
  private DateTimeOffset? _pressureSince;
  private DateTimeOffset? _quietSince;

  /// <summary>
  /// Creates a governor scaling between <paramref name="floor"/> and <paramref name="ceiling"/>.
  /// The floor is clamped into <c>[1, ceiling]</c>; concurrency starts at the (clamped) floor.
  /// </summary>
  /// <param name="floor">Minimum acceptor slots (<see cref="AzureServiceBusOptions.AcceptorFloor"/>).</param>
  /// <param name="ceiling">Maximum acceptor slots (<see cref="AzureServiceBusOptions.MaxConcurrentSessions"/>).</param>
  /// <param name="evaluationWindow">How long a pressure/quiet condition must hold before the pool scales.</param>
  /// <param name="timeProvider">Time source — injected so tests control the clock.</param>
  public AsbAcceptorGovernor(int floor, int ceiling, TimeSpan evaluationWindow, TimeProvider timeProvider) {
    ArgumentOutOfRangeException.ThrowIfLessThan(ceiling, 1);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(evaluationWindow, TimeSpan.Zero);
    ArgumentNullException.ThrowIfNull(timeProvider);

    Ceiling = ceiling;
    Floor = Math.Clamp(floor, 1, ceiling);
    _evaluationWindow = evaluationWindow;
    _timeProvider = timeProvider;
    _currentConcurrency = Floor;
  }

  /// <summary>Minimum acceptor slots — the pool never decays below this.</summary>
  public int Floor { get; }

  /// <summary>Maximum acceptor slots — growth never exceeds this.</summary>
  public int Ceiling { get; }

  /// <summary>The pool size the governor currently prescribes.</summary>
  public int CurrentConcurrency {
    get {
      lock (_sync) {
        return _currentConcurrency;
      }
    }
  }

  /// <summary>Sessions currently held open (initialized and not yet closed).</summary>
  public int ActiveSessions {
    get {
      lock (_sync) {
        return _activeSessions;
      }
    }
  }

  /// <summary>Records a session accept (the processor's session-initializing hook).</summary>
  public void OnSessionInitializing() {
    lock (_sync) {
      _activeSessions++;
    }
  }

  /// <summary>Records a session close (the processor's session-closing hook).</summary>
  public void OnSessionClosing() {
    lock (_sync) {
      if (_activeSessions > 0) {
        _activeSessions--;
      }
    }
  }

  /// <summary>
  /// One policy evaluation against the injected clock. Returns true when
  /// <see cref="CurrentConcurrency"/> changed — the caller then applies the new value to the
  /// running processor (<c>ServiceBusSessionProcessor.UpdateConcurrency</c>).
  /// </summary>
  public bool Evaluate() {
    var now = _timeProvider.GetUtcNow();
    lock (_sync) {
      var pressure = _activeSessions >= _currentConcurrency * GROWTH_PRESSURE_RATIO;
      var quiet = _activeSessions < _currentConcurrency * DECAY_IDLE_RATIO;
      _pressureSince = pressure ? _pressureSince ?? now : null;
      _quietSince = quiet ? _quietSince ?? now : null;

      if (_pressureSince is { } pressureStart
          && now - pressureStart >= _evaluationWindow
          && _currentConcurrency < Ceiling) {
        _currentConcurrency = Math.Min(_currentConcurrency * SCALE_FACTOR, Ceiling);
        _restartWindows();
        return true;
      }

      if (_quietSince is { } quietStart
          && now - quietStart >= _evaluationWindow
          && _currentConcurrency > Floor) {
        _currentConcurrency = Math.Max(_currentConcurrency / SCALE_FACTOR, Floor);
        _restartWindows();
        return true;
      }

      return false;
    }
  }

  /// <summary>A scale step resizes the pool — both conditions re-measure against the new size.</summary>
  private void _restartWindows() {
    _pressureSince = null;
    _quietSince = null;
  }
}
