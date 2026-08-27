using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Decides when periodic housekeeping may run, so it yields to live work and to itself.
/// </summary>
/// <remarks>
/// <para>
/// The heavy maintenance sweep runs on a fixed timer with no knowledge of what the service is
/// doing. Its statements take locks the completion path also needs, so a sweep landing mid-drain
/// puts the statement that MARKS WORK COMPLETE behind it. Workers keep claiming and processing and
/// then stall at the commit, leases stay held, and throughput collapses until the sweep finishes
/// and a burst of commits lands at once. Nothing errors, which is what makes it hard to attribute:
/// from the outside it reads as a freeze followed by a jump, on the sweep's cadence.
/// </para>
/// <para>
/// Deferring cleanup costs little. It has no deadline, and the moment its cost is highest is
/// exactly the moment it is least worth paying. What it does have is a limit — a service that
/// stays busy for hours must still reclaim space — so deferral is bounded rather than open-ended,
/// and a forced sweep is reported distinctly from a settled one because "this service never went
/// quiet" is itself worth surfacing.
/// </para>
/// <para>
/// Settledness is a SERVICE property, never an instance one. Many instances share one inbox, so an
/// instance that has finished its own slice looks idle from the inside while peers still hold
/// leases on the very rows the sweep would contend with. The measurement comes from the shared
/// store for that reason.
/// </para>
/// <para>
/// The two housekeeping activities are prioritized rather than merely serialized. Integrity work is
/// correctness-bearing and runs on a far tighter cadence, so it is never held back by a cleanup
/// sweep; a sweep deferred behind it simply runs on the next tick.
/// </para>
/// </remarks>
/// <docs>operations/workers/housekeeping-arbitration</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/HousekeepingCoordinatorTests.cs</tests>
public sealed class HousekeepingCoordinator {

  /// <summary>A periodic background activity that contends for store locks.</summary>
  public enum Activity {
    /// <summary>
    /// Stream-integrity work. Correctness-bearing and on a tight cadence, so it is not deferred
    /// behind cleanup.
    /// </summary>
    Integrity,

    /// <summary>The heavy cleanup sweep: reaping, pruning, and stale-row collection.</summary>
    Maintenance,
  }

  /// <summary>Why an activity was or was not allowed to start.</summary>
  public enum Verdict {
    /// <summary>The service is settled and nothing else holds the slot.</summary>
    Proceed,

    /// <summary>
    /// Settledness could not be measured, so prior behavior applies. A gate that cannot measure
    /// must never silently disable what it gates.
    /// </summary>
    ProceedUnmeasured,

    /// <summary>
    /// Allowed through after being deferred too many times consecutively. The service never went
    /// quiet, which is worth an operator's attention in its own right.
    /// </summary>
    ProceedDeferralLimit,

    /// <summary>Work is still queued or a peer instance holds leases.</summary>
    ServiceBusy,

    /// <summary>A higher-priority housekeeping activity holds the slot.</summary>
    HigherPriorityRunning,

    /// <summary>This activity is already running and must not stack a second copy.</summary>
    AlreadyRunning,
  }

  /// <summary>Tuning for <see cref="HousekeepingCoordinator"/>.</summary>
  public sealed class Settings {
    /// <summary>
    /// Consecutive deferrals tolerated before a sweep is forced through (default 6). At the default
    /// maintenance cadence that is an hour of sustained busyness before cleanup runs regardless.
    /// </summary>
    public int MaxConsecutiveDeferrals { get; set; } = 6;
  }

  /// <summary>The outcome of one admission request.</summary>
  /// <param name="Granted">Whether the activity may start.</param>
  /// <param name="Reason">Why, for logs and metrics.</param>
  public readonly record struct Decision(bool Granted, Verdict Reason);

  private readonly Settings _settings;
  private readonly object _gate = new();
  private bool _integrityRunning;
  private bool _maintenanceRunning;
  private int _consecutiveDeferrals;

  /// <summary>
  /// Initializes a new instance of the <see cref="HousekeepingCoordinator"/> class with default
  /// tuning. This is the constructor container registration uses; a host wanting different tuning
  /// registers its own instance, which the framework's TryAdd defers to.
  /// </summary>
  public HousekeepingCoordinator() : this(new Settings()) { }

  /// <summary>Initializes a new instance of the <see cref="HousekeepingCoordinator"/> class.</summary>
  /// <param name="settings">Tuning; defaults are production-safe.</param>
  public HousekeepingCoordinator(Settings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    _settings = settings;
  }

  /// <summary>Requests permission to start <paramref name="activity"/>.</summary>
  /// <param name="activity">The housekeeping activity about to run.</param>
  /// <param name="backlog">
  /// Service-wide settledness from the shared store, or null when the backend cannot report it.
  /// </param>
  /// <returns>Whether to proceed, and why.</returns>
  public Decision TryBegin(Activity activity, ServiceBacklog? backlog) {
    lock (_gate) {
      if (activity == Activity.Integrity) {
        // Integrity is not gated on settledness here — the checkpoint path applies its own, which
        // distinguishes a lagging consumer from a genuine deficit. This guards only against a cycle
        // overlapping itself.
        if (_integrityRunning) {
          return new Decision(false, Verdict.AlreadyRunning);
        }
        _integrityRunning = true;
        return new Decision(true, Verdict.Proceed);
      }

      if (_maintenanceRunning) {
        return new Decision(false, Verdict.AlreadyRunning);
      }

      // Asymmetric by design: cleanup waits for integrity, never the reverse.
      if (_integrityRunning) {
        return new Decision(false, Verdict.HigherPriorityRunning);
      }

      if (backlog is null) {
        _maintenanceRunning = true;
        return new Decision(true, Verdict.ProceedUnmeasured);
      }

      if (!backlog.IsSettled) {
        _consecutiveDeferrals++;
        if (_consecutiveDeferrals > _settings.MaxConsecutiveDeferrals) {
          // Bounded deferral. Space has to be reclaimed eventually, so an indefinitely busy service
          // gets its sweep anyway — reported distinctly, because reaching this branch means the
          // service has not settled once across the whole window.
          _maintenanceRunning = true;
          return new Decision(true, Verdict.ProceedDeferralLimit);
        }
        return new Decision(false, Verdict.ServiceBusy);
      }

      _maintenanceRunning = true;
      return new Decision(true, Verdict.Proceed);
    }
  }

  /// <summary>
  /// Claims the slot for integrity work for the lifetime of the returned scope.
  /// </summary>
  /// <remarks>
  /// Exclusion only exists if the higher-priority activity actually announces itself, and a manual
  /// begin/end pair around a cycle that can throw is a slot leak waiting to happen — a leaked slot
  /// disables the cleanup sweep for the life of the process. Disposing a REFUSED scope is a no-op,
  /// so a second concurrent cycle cannot hand away the slot the first one still holds.
  /// </remarks>
  /// <returns>A scope that releases the slot on dispose.</returns>
  public IntegrityScope BeginIntegrityScope() {
    var decision = TryBegin(Activity.Integrity, backlog: null);
    return new IntegrityScope(this, decision.Granted);
  }

  /// <summary>A scoped hold on the integrity slot.</summary>
  public readonly struct IntegrityScope : IDisposable, IEquatable<IntegrityScope> {
    private readonly HousekeepingCoordinator? _owner;

    internal IntegrityScope(HousekeepingCoordinator owner, bool granted) {
      _owner = granted ? owner : null;
      Granted = granted;
    }

    /// <summary>Whether this caller actually took the slot.</summary>
    public bool Granted { get; }

    /// <summary>Releases the slot, if this scope holds it.</summary>
    public void Dispose() => _owner?.End(Activity.Integrity);

    /// <inheritdoc />
    public bool Equals(IntegrityScope other) => ReferenceEquals(_owner, other._owner) && Granted == other.Granted;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IntegrityScope other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_owner, Granted);

    /// <summary>Equality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>True when equal.</returns>
    public static bool operator ==(IntegrityScope left, IntegrityScope right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>True when not equal.</returns>
    public static bool operator !=(IntegrityScope left, IntegrityScope right) => !left.Equals(right);
  }

  /// <summary>Releases the slot held by <paramref name="activity"/>.</summary>
  /// <remarks>
  /// Releasing an activity that never started is harmless and must not hand away another
  /// activity's slot — a stray release cancelling someone else's exclusion would reintroduce the
  /// overlap this type exists to prevent.
  /// </remarks>
  /// <param name="activity">The activity that has finished.</param>
  public void End(Activity activity) {
    lock (_gate) {
      if (activity == Activity.Integrity) {
        _integrityRunning = false;
        return;
      }

      if (_maintenanceRunning) {
        _maintenanceRunning = false;
        // Reset only on a sweep that actually ran. Resetting on a stray release would let the
        // forced-through branch re-arm every cycle, which is the un-gated behavior it bounds.
        _consecutiveDeferrals = 0;
      }
    }
  }
}
