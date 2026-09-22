namespace Whizbang.Core.Startup;

/// <summary>
/// Per-duty retry damping for <see cref="IDutyElector"/> implementations. A contended attempt
/// (another instance holds the duty) opens a suppression window; while the window is open the
/// elector answers "contended" from memory instead of taking a round trip to the coordination
/// store. Consecutive contended attempts double the window from the floor up to the ceiling; a
/// grant, or any refusal that is not contention, resets it.
/// </summary>
/// <remarks>
/// Callers decide their own re-attempt cadence, and several of them retry on a tight loop by
/// design (a leader duty must fail over quickly). On an idle fleet that turned into a steady stream
/// of <c>pg_try_advisory_lock</c> calls that could never succeed while the holder was healthy. The
/// window bounds that cost without changing the callers: failover latency after the holder dies is
/// at most one ceiling, and the ceiling is derived from an existing option, not a new knob.
/// </remarks>
/// <docs>operations/startup/capabilities-and-duties</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/DutyContentionBackoffTests.cs</tests>
public sealed class DutyContentionBackoff {
  private readonly TimeSpan _floor;
  private readonly TimeSpan _ceiling;
  private TimeSpan _nextWindow;
  private DateTimeOffset? _suppressedUntil;

  /// <summary>Creates the damper.</summary>
  /// <param name="floor">The first suppression window after a contended attempt.</param>
  /// <param name="ceiling">The longest suppression window.</param>
  /// <exception cref="ArgumentOutOfRangeException">Thrown when the floor is not positive or the ceiling is below the floor.</exception>
  public DutyContentionBackoff(TimeSpan floor, TimeSpan ceiling) {
    if (floor <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(floor), floor, "The floor must be positive.");
    }
    if (ceiling < floor) {
      throw new ArgumentOutOfRangeException(nameof(ceiling), ceiling, "The ceiling must be at least the floor.");
    }
    _floor = floor;
    _ceiling = ceiling;
    _nextWindow = floor;
  }

  /// <summary>How many contended attempts in a row have been recorded since the last reset.</summary>
  public int ConsecutiveContentions { get; private set; }

  /// <summary>
  /// True while a suppression window is open; <paramref name="remaining"/> says how long it stays open.
  /// </summary>
  /// <param name="now">The current time.</param>
  /// <param name="remaining">Time left in the window, or zero when none is open.</param>
  /// <returns>True when attempts should be answered from memory.</returns>
  public bool IsSuppressed(DateTimeOffset now, out TimeSpan remaining) {
    if (_suppressedUntil is { } until && until > now) {
      remaining = until - now;
      return true;
    }
    remaining = TimeSpan.Zero;
    return false;
  }

  /// <summary>Records a contended attempt and opens the next window.</summary>
  /// <param name="now">The current time.</param>
  /// <returns>The window that was opened.</returns>
  public TimeSpan RecordContended(DateTimeOffset now) {
    ConsecutiveContentions++;
    var window = _nextWindow;
    _suppressedUntil = now + window;
    var doubled = TimeSpan.FromTicks(Math.Min(window.Ticks * 2, _ceiling.Ticks));
    _nextWindow = doubled;
    return window;
  }

  /// <summary>Clears the window and the streak: the duty was granted, or refused for a reason a retry cannot change.</summary>
  public void Reset() {
    ConsecutiveContentions = 0;
    _nextWindow = _floor;
    _suppressedUntil = null;
  }
}
