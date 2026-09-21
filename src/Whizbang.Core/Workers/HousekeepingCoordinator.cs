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
/// <tests>tests/Whizbang.Core.Tests/Workers/RecoveryLifecycleHardeningTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkerOptionsBindingTests.cs:HousekeepingDeferralLimit_ReachesTheArbitrationMechanismAsync</tests>
public sealed class HousekeepingCoordinator {

  /// <summary>A periodic background activity that contends for store locks.</summary>
  public enum Activity {
    /// <summary>
    /// Re-driving dead-lettered messages. The highest rank, because the dead-letter table
    /// frequently CONTAINS the very messages integrity detects as gaps.
    /// </summary>
    /// <remarks>
    /// A message fails, lands in the dead-letter table, and the resulting deficit is what the
    /// checkpoint path later confirms as a gap. Asking the origin to redeliver over the wire what
    /// is already sitting locally is the expensive way to heal that, and it is how a backlog
    /// becomes cross-service replay traffic. Recovering first closes the gap at its source and
    /// removes the reason to ask; integrity deferred behind it runs on its next tick.
    /// </remarks>
    DeadLetterRecovery,

    /// <summary>
    /// Stream-integrity work. Correctness-bearing and on a tight cadence, so it is not deferred
    /// behind cleanup.
    /// </summary>
    Integrity,

    /// <summary>The heavy cleanup sweep: reaping, pruning, and stale-row collection.</summary>
    Maintenance,
  }

  /// <summary>Rank of an activity; lower wins the slot.</summary>
  private static int _rank(Activity activity) => activity switch {
    Activity.DeadLetterRecovery => 0,
    Activity.Integrity => 1,
    _ => 2,
  };

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

    /// <summary>
    /// The service reads settled, but not for long enough yet. Distinct from
    /// <see cref="ServiceBusy"/> because the two call for opposite operator responses: busy means
    /// the service has more work than it can finish, cooling down means it has just finished.
    /// </summary>
    ServiceCoolingDown,

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

    /// <summary>
    /// How long the service must read settled CONTINUOUSLY before a sweep is admitted (default two
    /// minutes). Zero admits on the first settled reading, which is the pre-cooldown behavior.
    /// </summary>
    /// <remarks>
    /// Settledness is a sample, and a sample is not a state. A pipeline working through a bulk load
    /// empties its work tables between bursts, so an instant reading of zero says only "nothing is
    /// queued right now" — and cleanup admitted on that reading starts a sweep of tens of seconds
    /// just as the next burst lands, which is the case the gate exists to avoid. Requiring the
    /// reading to hold for a dwell distinguishes a trough from an ending.
    /// </remarks>
    public TimeSpan SettledCooldown { get; set; } = TimeSpan.FromMinutes(2);
  }

  /// <summary>The outcome of one admission request.</summary>
  /// <param name="Granted">Whether the activity may start.</param>
  /// <param name="Reason">Why, for logs and metrics.</param>
  public readonly record struct Decision(bool Granted, Verdict Reason);

  private readonly Settings _settings;
  private readonly TimeProvider _timeProvider;
  private readonly Observability.HousekeepingMetrics? _metrics;
  private readonly Lock _gate = new();
  private bool _dlqRunning;
  private bool _integrityRunning;
  private bool _maintenanceRunning;
  private int _consecutiveDeferrals;
  private int _recoveryDeferrals;

  // When the service most recently STARTED reading settled, or null while it reads unsettled. Every
  // unsettled reading clears it, so the dwell measures an unbroken run of settled readings rather
  // than the time since the first one ever seen.
  private DateTimeOffset? _settledSince;

  // The LOOSER reading, kept for the idle band: whether the last measurement showed no work anyone
  // waits for, with the idle band itself excluded. Separate from _settledSince, which is the
  // quiescent dwell that gates sweeps -- the band must be admitted while it still holds rows, and
  // the dwell by construction never is.
  private bool _serviceReadsSettled;
  private DateTimeOffset? _serviceSettledReadAt;

  /// <summary>
  /// Initializes a new instance of the <see cref="HousekeepingCoordinator"/> class with default
  /// tuning. This is the constructor container registration uses; a host wanting different tuning
  /// registers its own instance, which the framework's TryAdd defers to.
  /// </summary>
  public HousekeepingCoordinator() : this(new Settings()) { }

  /// <summary>Initializes a new instance with metrics.</summary>
  /// <param name="settings">Tuning.</param>
  /// <param name="metrics">Optional observability; null records nothing.</param>
  public HousekeepingCoordinator(Settings settings, Observability.HousekeepingMetrics? metrics) : this(settings) {
    _metrics = metrics;
  }

  /// <summary>Initializes a new instance of the <see cref="HousekeepingCoordinator"/> class.</summary>
  /// <param name="settings">Tuning; defaults are production-safe.</param>
  public HousekeepingCoordinator(Settings settings) : this(settings, TimeProvider.System) { }

  /// <summary>Initializes a new instance with an explicit clock.</summary>
  /// <param name="settings">Tuning; defaults are production-safe.</param>
  /// <param name="timeProvider">The clock the settled dwell is measured on.</param>
  public HousekeepingCoordinator(Settings settings, TimeProvider timeProvider) {
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _settings = settings;
    _timeProvider = timeProvider;
  }

  /// <summary>
  /// Records a settledness reading without asking to start anything, so the dwell is measured from
  /// readings taken between sweeps rather than only the one taken when a sweep is due.
  /// </summary>
  /// <param name="backlog">The reading, or null when the backend could not report one.</param>
  /// <remarks>
  /// A gate that samples only when it wants to run cannot tell a trough from an ending: its
  /// resolution is its own interval. Any worker holding a fresh reading can feed it here.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Workers/HousekeepingCooldownTests.cs</tests>
  public void Observe(ServiceBacklog? backlog) {
    lock (_gate) {
      _trackSettled(backlog);
    }
  }

  // Caller holds _gate. Null (unmeasured) does not clear the dwell: a reading that could not be
  // taken is not evidence of work, and treating it as one would let a flaky probe starve cleanup.
  private void _trackSettled(ServiceBacklog? backlog) {
    if (backlog is null) {
      return;
    }
    _serviceReadsSettled = backlog.IsSettled;
    _serviceSettledReadAt = _timeProvider.GetUtcNow();
    // Quiescent, not merely settled (167). Settled is the signal that ADMITS the idle drain, so a
    // sweep gated on it would start at the moment the drain does and compete with it for the same
    // backends -- the very pile-up the gate exists to prevent. Maintenance is the one caller that
    // can afford to queue behind the idle band, because MaxConsecutiveDeferrals already bounds how
    // long it will wait before forcing a pass through anyway.
    if (!backlog.IsQuiescent) {
      _settledSince = null;
      return;
    }
    _settledSince ??= _timeProvider.GetUtcNow();
  }

  // Caller holds _gate.
  private bool _cooldownElapsed() {
    if (_settings.SettledCooldown <= TimeSpan.Zero) {
      return true;
    }
    return _settledSince is { } since
      && _timeProvider.GetUtcNow() - since >= _settings.SettledCooldown;
  }

  /// <summary>
  /// Whether the latest service-wide reading showed the service settled, provided that reading is
  /// no older than <paramref name="maxAge"/>. This is the signal that admits the idle band at full
  /// width.
  /// </summary>
  /// <param name="maxAge">
  /// How stale a reading may be and still be acted on. Readings arrive on the maintenance cadence,
  /// not the claim's, so an unbounded answer would drain the band at full width off a measurement
  /// taken before the load that is running now.
  /// </param>
  /// <remarks>
  /// Deliberately the looser <see cref="ServiceBacklog.IsSettled"/> and not
  /// <see cref="ServiceBacklog.IsQuiescent"/>: the band is admitted BECAUSE it still holds rows,
  /// so a signal that required them to be gone could never admit it.
  /// </remarks>
  public bool ServiceReadsSettled(TimeSpan maxAge) {
    lock (_gate) {
      return _serviceReadsSettled
        && _serviceSettledReadAt is { } at
        && _timeProvider.GetUtcNow() - at <= maxAge;
    }
  }

  /// <summary>Requests permission to start <paramref name="activity"/>.</summary>
  /// <param name="activity">The housekeeping activity about to run.</param>
  /// <param name="backlog">
  /// Service-wide settledness from the shared store, or null when the backend cannot report it.
  /// </param>
  /// <returns>Whether to proceed, and why.</returns>
  public Decision TryBegin(Activity activity, ServiceBacklog? backlog) {
    var decision = _tryBeginCore(activity, backlog);
    _metrics?.RecordDecision(activity, decision.Reason, decision.Granted);
    return decision;
  }

  private Decision _tryBeginCore(Activity activity, ServiceBacklog? backlog) {
    lock (_gate) {
      // Every reading feeds the dwell, including the ones that go on to be refused for an unrelated
      // reason: settledness is a property of the service, not of this admission request.
      _trackSettled(backlog);

      // Self-overlap is refused for every activity: a cycle must never race itself.
      var alreadyRunning = activity switch {
        Activity.DeadLetterRecovery => _dlqRunning,
        Activity.Integrity => _integrityRunning,
        _ => _maintenanceRunning,
      };
      if (alreadyRunning) {
        return new Decision(false, Verdict.AlreadyRunning);
      }

      // Ranked, not merely serialized: an activity yields only to a STRICTLY higher rank, so
      // recovery never waits behind integrity or cleanup, and integrity never waits behind cleanup.
      var rank = _rank(activity);
      if ((_dlqRunning && rank > _rank(Activity.DeadLetterRecovery))
          || (_integrityRunning && rank > _rank(Activity.Integrity))) {
        return new Decision(false, Verdict.HigherPriorityRunning);
      }

      if (activity == Activity.Integrity) {
        // Integrity is not gated on settledness here — the checkpoint path applies its own, which
        // distinguishes a lagging consumer from a genuine deficit.
        _integrityRunning = true;
        return new Decision(true, Verdict.Proceed);
      }

      // Recovery and cleanup ask the same question of the service and differ only in which budget
      // they spend and which slot they take, so they share one admission rule rather than two
      // copies of it that can drift.
      if (activity == Activity.DeadLetterRecovery) {
        var recovery = _admitWhenQuiet(backlog, ref _recoveryDeferrals);
        if (recovery.Granted) {
          _dlqRunning = true;
        }
        return recovery;
      }

      var maintenance = _admitWhenQuiet(backlog, ref _consecutiveDeferrals);
      if (maintenance.Granted) {
        _maintenanceRunning = true;
      }
      return maintenance;
    }
  }

  /// <summary>
  /// The settledness rule both deferrable activities apply, over the caller's own deferral budget.
  /// Decides only whether to admit; the caller claims its own slot.
  /// </summary>
  /// <remarks>
  /// Unmeasured proceeds: a gate that cannot measure must never silently disable what it gates.
  /// Busy and cooling-down both spend the budget, so a service that alternates between working and
  /// brief troughs still reaches its forced pass instead of deferring forever — observed once as
  /// 20,000 due dead letters behind a service whose backlog never touched zero. The forced pass is
  /// reported distinctly, because "this service never went quiet" is worth an operator's attention
  /// on its own. The budget re-arms on End, so this is a trickle under load, never an open gate.
  /// </remarks>
  private Decision _admitWhenQuiet(ServiceBacklog? backlog, ref int deferrals) {
    if (backlog is null) {
      return new Decision(true, Verdict.ProceedUnmeasured);
    }

    Verdict reason;
    // IsQuiescent is what IsSettled meant before 167 -- every count at zero, the idle band
    // included. Sweeps keep that meaning exactly; only the idle drain itself runs on the looser
    // signal, or it could never be admitted.
    if (!backlog.IsQuiescent) {
      reason = Verdict.ServiceBusy;
    } else if (!_cooldownElapsed()) {
      reason = Verdict.ServiceCoolingDown;
    } else {
      return new Decision(true, Verdict.Proceed);
    }

    deferrals++;
    return deferrals > _settings.MaxConsecutiveDeferrals
      ? new Decision(true, Verdict.ProceedDeferralLimit)
      : new Decision(false, reason);
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
    _metrics?.RecordEnd(activity);
    lock (_gate) {
      if (activity == Activity.DeadLetterRecovery) {
        _dlqRunning = false;
        // Mirror of maintenance below: a recovery pass that actually ran re-arms the budget, so
        // the forced-through branch cannot re-open every cycle on a service that stays busy.
        _recoveryDeferrals = 0;
        return;
      }

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
