namespace Whizbang.Core.Messaging;

/// <summary>
/// Decides whether a confirmed integrity deficit should actually be repaired.
/// </summary>
/// <remarks>
/// <para>
/// Checkpoint gap detection confirms a deficit that survives two consecutive checkpoints. At the
/// default cadence that is a sixty-second window, which is long enough to absorb a straggler on a
/// healthy consumer and far too short for one that is behind. A consumer running minutes or hours
/// back therefore reports the same deficit on both checks and confirms a gap while nothing has been
/// lost at all.
/// </para>
/// <para>
/// The scheduled deep audit already guards against exactly this, folding only events older than a
/// settle window because "an in-flight delivery must never read as divergence." The checkpoint path
/// had no equivalent. This type supplies it.
/// </para>
/// <para>
/// The cost of getting it wrong is not a wasted request. Repair re-delivers the window; on a
/// backlogged consumer those events queue behind everything already waiting and cannot arrive
/// before the next confirmation either; the deficit re-confirms and repair fires again. Every cycle
/// ADDS load, which increases lag, which manufactures more false gaps — a consumer can end up
/// emitting many times the traffic its producer did, entirely from self-inflicted repair.
/// </para>
/// <para>
/// Two independent signals gate a repair, because either alone is fooled. Queue DEPTH is a snapshot
/// and reads zero in the gap between claim cycles even mid-storm. Consumer LAG persists across that
/// window but can look healthy on a service that is idle for unrelated reasons. Repair proceeds only
/// when both say the consumer has settled.
/// </para>
/// <para>
/// Beyond that, repair stops asking when asking is not working: a window that has been requested
/// repeatedly without healing has exhausted its budget, and a global bound limits how many windows
/// may be under repair at once. A per-checkpoint cap cannot do this — it bounds one checkpoint,
/// while checkpoints keep arriving on cadence.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityRepairSpinTests.cs</tests>
public sealed class IntegrityRepairPolicy {

  /// <summary>Why a repair was or was not requested.</summary>
  public enum Verdict {
    /// <summary>The consumer has settled and the deficit looks real — repair.</summary>
    Repair,

    /// <summary>Work is still queued or the consumer is lagging; the events are late, not lost.</summary>
    ConsumerBehind,

    /// <summary>This window has been requested repeatedly without healing.</summary>
    AttemptsExhausted,

    /// <summary>Too many windows are already under repair.</summary>
    GlobalBudgetExhausted,
  }

  /// <summary>Tuning for <see cref="IntegrityRepairPolicy"/>.</summary>
  public sealed class Settings {
    /// <summary>
    /// Service-wide queued items above which the consumer counts as behind. Default zero: any
    /// queued work means the missing events may simply be waiting their turn.
    /// </summary>
    public int SettledBacklogThreshold { get; set; }

    /// <summary>
    /// Live leases held by ANY instance above which the service counts as busy. Default zero: one
    /// peer mid-dispatch is enough, because the events being counted as missing may be in its
    /// hands right now.
    /// </summary>
    public int SettledActiveLeaseThreshold { get; set; }

    /// <summary>
    /// Lag above which the consumer counts as behind regardless of queue depth (default 2 minutes,
    /// comfortably beyond the default checkpoint cadence).
    /// </summary>
    public TimeSpan SettledLagThreshold { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Requests for one window before giving up on it (default 3).</summary>
    public int MaxAttemptsPerWindow { get; set; } = 3;

    /// <summary>Windows that may be under repair simultaneously (default 8).</summary>
    public int MaxConcurrentWindowsUnderRepair { get; set; } = 8;
  }

  /// <summary>One confirmed deficit, plus how settled the SERVICE is.</summary>
  /// <remarks>
  /// <para>
  /// Every settledness field describes the whole service, never the evaluating instance. A service
  /// runs many instances against one shared inbox, and an instance that has finished its own
  /// claimed streams looks completely idle from the inside while peers are still draining. An
  /// instance deciding from its local view re-requests events its own siblings are actively
  /// processing — the storm returning through whichever replica happened to be free.
  /// </para>
  /// <para>
  /// Populate these from the shared store, which is the only place the service-wide answer exists:
  /// unprocessed rows across the whole inbox, and live leases held by ANY instance.
  /// </para>
  /// </remarks>
  /// <param name="OriginServiceId">Origin the checkpoint came from.</param>
  /// <param name="EventType">Wire-form event type the deficit is in.</param>
  /// <param name="TenantScope">Tenant bucket, when scoped.</param>
  /// <param name="FromCommitSequence">Window start, exclusive.</param>
  /// <param name="ToCommitSequence">Window end, inclusive.</param>
  /// <param name="ExpectedCount">What the origin says it published.</param>
  /// <param name="ActualCount">What this consumer has received.</param>
  /// <param name="ServiceBacklogDepth">
  /// Unprocessed rows across the ENTIRE service inbox — not this instance's share.
  /// </param>
  /// <param name="ConsumerLag">How far behind the service is running.</param>
  /// <param name="ActiveLeaseCount">
  /// Rows currently leased by ANY instance of this service. Non-zero means a peer is mid-dispatch,
  /// so the events counted as missing may simply be in that peer's hands.
  /// </param>
  public sealed record GapObservation(
    Guid OriginServiceId,
    string EventType,
    string? TenantScope,
    long FromCommitSequence,
    long ToCommitSequence,
    int ExpectedCount,
    int ActualCount,
    int ServiceBacklogDepth,
    TimeSpan ConsumerLag,
    int ActiveLeaseCount);

  /// <summary>The outcome of evaluating one observation.</summary>
  /// <param name="ShouldRequestRepair">Whether to send a redelivery request.</param>
  /// <param name="Reason">Why, for logs and metrics.</param>
  public readonly record struct Decision(bool ShouldRequestRepair, Verdict Reason);

  private sealed record WindowState(int Attempts, int BestActualCount);

  private readonly Settings _settings;
  private readonly Dictionary<string, WindowState> _windows = [];

  /// <summary>Initializes a new instance of the <see cref="IntegrityRepairPolicy"/> class.</summary>
  /// <param name="settings">Tuning; defaults are production-safe.</param>
  public IntegrityRepairPolicy(Settings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    _settings = settings;
  }

  /// <summary>Decides whether <paramref name="observation"/> warrants a repair request.</summary>
  /// <param name="observation">The confirmed deficit and the consumer's settledness.</param>
  /// <returns>The decision and the reason behind it.</returns>
  public Decision Evaluate(GapObservation observation) {
    ArgumentNullException.ThrowIfNull(observation);

    // Settledness first, and it is not overridable by how large the deficit looks. A big apparent
    // gap on a backlogged consumer is the STRONGEST evidence of lag, not the strongest case for
    // repair — treating it as urgent is what turns a slow consumer into a storm.
    //
    // All three signals are SERVICE-wide and any one of them vetoes. Depth is a snapshot and reads
    // zero between claim cycles. Lag survives that but looks healthy on a service idle for
    // unrelated reasons. Live leases are what catch the case neither can: this instance finished
    // its slice and sees nothing locally, while peers are still dispatching the very events being
    // counted as missing.
    if (observation.ServiceBacklogDepth > _settings.SettledBacklogThreshold
        || observation.ConsumerLag > _settings.SettledLagThreshold
        || observation.ActiveLeaseCount > _settings.SettledActiveLeaseThreshold) {
      return new Decision(false, Verdict.ConsumerBehind);
    }

    var key = _key(observation);
    if (_windows.TryGetValue(key, out var state)) {
      // Healing counts as working: if more of the window has landed since the last request, the
      // budget resets rather than expiring mid-recovery.
      if (observation.ActualCount > state.BestActualCount) {
        _windows[key] = new WindowState(0, observation.ActualCount);
      } else if (state.Attempts >= _settings.MaxAttemptsPerWindow) {
        return new Decision(false, Verdict.AttemptsExhausted);
      }
    }

    // A global bound is the only thing that limits the RATE at which repair adds load. The
    // per-checkpoint cap bounds a single checkpoint, and checkpoints keep arriving on cadence.
    if (!_windows.ContainsKey(key) && _windows.Count >= _settings.MaxConcurrentWindowsUnderRepair) {
      return new Decision(false, Verdict.GlobalBudgetExhausted);
    }

    return new Decision(true, Verdict.Repair);
  }

  /// <summary>Records that a repair request was actually sent for this window.</summary>
  /// <param name="observation">The window that was requested.</param>
  public void RecordRequested(GapObservation observation) {
    ArgumentNullException.ThrowIfNull(observation);
    var key = _key(observation);
    var state = _windows.TryGetValue(key, out var existing)
      ? existing
      : new WindowState(0, observation.ActualCount);
    _windows[key] = state with { Attempts = state.Attempts + 1 };
  }

  /// <summary>Releases a window's slot once it has healed.</summary>
  /// <param name="observation">The window that is now complete.</param>
  public void RecordHealed(GapObservation observation) {
    ArgumentNullException.ThrowIfNull(observation);
    _windows.Remove(_key(observation));
  }

  private static string _key(GapObservation o) =>
    string.Create(System.Globalization.CultureInfo.InvariantCulture,
      $"{o.OriginServiceId:N}|{o.TenantScope}|{o.EventType}|{o.FromCommitSequence}|{o.ToCommitSequence}");
}
